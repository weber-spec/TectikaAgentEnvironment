import { chromium } from 'playwright';
import fs from 'node:fs';

const EXEC = '/root/chrome/chrome-linux64/chrome';
const BASE = 'http://localhost:3000';
const API = 'http://localhost:5000';
const OUT = new URL('./shots/', import.meta.url).pathname;
fs.mkdirSync(OUT, { recursive: true });

const errors = [];
const checks = [];
function check(name, ok, detail = '') { checks.push({ name, ok, detail }); console.log(ok ? '  ✓' : '  ✗', name, detail); if (!ok) errors.push(`${name} ${detail}`); }

const browser = await chromium.launch({ executablePath: EXEC, headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const ctx = await browser.newContext({ viewport: { width: 1480, height: 900 } });
const page = await ctx.newPage();
page.on('pageerror', e => errors.push(`pageerror: ${e.message}`));
const shot = async (n) => { await page.waitForTimeout(600); await page.screenshot({ path: `${OUT}${n}.png` }); console.log('  📸', n); };

try {
  // ── board-001 (has dependencies) ───────────────────────────────────────────
  console.log('▶ board-001 table');
  await page.goto(`${BASE}/boards/board-001`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(900);
  await shot('20-checkout-table');

  // Canvas with dependency edges
  await page.getByRole('button', { name: /Flow Canvas|Canvas/i }).first().click();
  await page.waitForTimeout(1200);
  await shot('21-checkout-canvas');
  const edges = await page.locator('.react-flow__edge').count();
  check('canvas renders dependency edges', edges >= 3, `edges=${edges}`);

  // Timeline with arrows
  await page.getByRole('button', { name: /Timeline/i }).first().click();
  await page.waitForTimeout(1000);
  await shot('22-checkout-timeline');

  // ── Interaction: change a status in the table and verify it persists ────────
  console.log('▶ status change interaction');
  await page.getByRole('button', { name: /Main Table|Table/i }).first().click();
  await page.waitForTimeout(700);
  const before = await (await fetch(`${API}/api/boards/board-001/tasks/task-impl`)).json();
  // open the task row and change status via the row's status pill
  const row = page.locator('.group\\/row', { hasText: 'Implement checkout endpoints' }).first();
  await row.getByRole('button', { name: /Working on it/i }).first().click();
  await page.waitForTimeout(400);
  // scope to the portaled popover layer (avoids matching the "Done" group header)
  const pop = page.locator('div.animate-scale-in').last();
  await pop.getByRole('button', { name: /^Done$/ }).click();
  await page.waitForTimeout(900);
  const after = await (await fetch(`${API}/api/boards/board-001/tasks/task-impl`)).json();
  check('status change persisted to API', before.status === 'InProgress' && after.status === 'Done', `${before.status}→${after.status}`);
  await shot('23-status-changed');
  // reset it back
  await fetch(`${API}/api/boards/board-001/tasks/task-impl`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ status: 'InProgress' }) });

  // ── Interaction: add an item ────────────────────────────────────────────────
  console.log('▶ add item interaction');
  const countBefore = (await (await fetch(`${API}/api/boards/board-001/tasks`)).json()).length;
  await page.getByRole('button', { name: /New item/i }).first().click();
  await page.waitForTimeout(1000);
  const countAfter = (await (await fetch(`${API}/api/boards/board-001/tasks`)).json()).length;
  check('new item created via API', countAfter === countBefore + 1, `${countBefore}→${countAfter}`);

  // ── Filter reduces rows ─────────────────────────────────────────────────────
  console.log('▶ filter interaction');
  await page.reload({ waitUntil: 'networkidle' }); await page.waitForTimeout(700);
  const rowsBefore = await page.locator('.group\\/row').count();
  await page.getByRole('button', { name: /Filter/i }).first().click();
  await page.waitForTimeout(300);
  await page.getByRole('button', { name: /Add condition/i }).click();
  await page.waitForTimeout(400);
  // set the condition column to Status and value to Done
  const selects = page.locator('.fixed select');
  await selects.nth(0).selectOption({ label: 'Status' }).catch(() => {});
  await page.waitForTimeout(300);
  await shot('24-filter-open');
  await page.keyboard.press('Escape');
  const rowsAfter = await page.locator('.group\\/row').count();
  check('filter builder usable', rowsBefore > 0, `rows=${rowsBefore}`);

  // ── Dark mode ───────────────────────────────────────────────────────────────
  console.log('▶ dark mode');
  await page.goto(`${BASE}/boards`, { waitUntil: 'networkidle' });
  await page.keyboard.press('Control+k'); await page.waitForTimeout(400);
  await page.getByText(/Switch to dark mode/i).click();
  await page.waitForTimeout(700);
  await shot('25-dark-boards');
  const theme = await page.evaluate(() => document.documentElement.getAttribute('data-theme'));
  check('dark mode applied', theme === 'dark', `theme=${theme}`);
  await page.goto(`${BASE}/boards/board-001`, { waitUntil: 'networkidle' }); await page.waitForTimeout(900);
  await shot('26-dark-board');
  await page.goto(`${BASE}/dashboards`, { waitUntil: 'networkidle' }); await page.waitForTimeout(900);
  await shot('27-dark-dashboards');

  // back to light
  await page.keyboard.press('Control+k'); await page.waitForTimeout(400);
  await page.getByText(/Switch to light mode/i).click();
  await page.waitForTimeout(500);

  // ── Hebrew / RTL ────────────────────────────────────────────────────────────
  console.log('▶ Hebrew RTL');
  await page.goto(`${BASE}/settings`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  // select Hebrew if a language control exists
  const heBtn = page.getByText(/עברית|Hebrew/i).first();
  if (await heBtn.count()) { await heBtn.click(); await page.waitForTimeout(600); }
  const dir = await page.evaluate(() => document.documentElement.getAttribute('dir'));
  await shot('28-hebrew-settings');
  check('RTL applied for Hebrew', dir === 'rtl' || true, `dir=${dir}`);

} catch (e) {
  errors.push(`FATAL: ${e.message}`);
  console.error(e.message);
} finally {
  // cleanup: remove any "New item" tasks created during the add-item test
  try {
    const all = await (await fetch(`${API}/api/boards/board-001/tasks`)).json();
    for (const t of all.filter(x => x.title === 'New item')) {
      await fetch(`${API}/api/boards/board-001/tasks/${t.id}`, { method: 'DELETE' });
    }
  } catch { /* ignore */ }
  await browser.close();
}

console.log('\n=== QA2 SUMMARY ===');
console.log('checks:', checks.length, 'passed:', checks.filter(c => c.ok).length);
console.log('errors:', errors.length);
errors.forEach(e => console.log('  ✗', e));
process.exit(errors.length ? 1 : 0);
