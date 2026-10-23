// M10 demo mode. run.mjs starts the app with Demo:Enabled = true on a throwaway data directory.
// A visitor starts a demo from the login page, gets an own account with fictional data, sees the
// banner on every page, and cannot upload files. A second visitor never sees the first one's changes.
import { chromium } from 'playwright';

const BASE = 'http://localhost:5299';
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

const browser = await chromium.launch({ channel: 'msedge' });
const errors = [];

async function startDemo() {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  // 401 before login / after logout and the deliberately refused 403 requests are expected.
  page.on('console', (m) => { if (m.type() === 'error' && !/status of (401|403)/.test(m.text())) errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(e.message));
  // Since M11 "/" leads to the start page (m11.mjs); this suite uses the demo button on the login page.
  await page.goto(BASE + '/login.html');
  await page.waitForSelector('#demo-section:not([hidden])');
  await page.click('#demo-start');
  await page.waitForURL((url) => url.pathname === '/' || url.pathname === '/index.html');
  return { context, page };
}

// ---------- Login page and start ----------
const { context, page } = await startDemo();
check(await page.locator('#register-link').isHidden(), 'registration link hidden in demo mode');
await page.waitForSelector('.demo-banner');
check((await page.locator('.demo-banner').innerText()).includes('fiktiv'), 'dashboard shows the demo banner');
check((await page.locator('[data-user-email]').innerText()) === 'Demo-Konto', 'sidebar shows "Demo-Konto" instead of the address');
check(await page.locator('[data-change-password]').count() === 0, 'no "Passwort ändern" for demo accounts');

// ---------- Every page works with the demo data ----------
const pages = [
  ['/bewerbungen.html', 'tr[data-id]'],
  ['/pipeline.html', '#board [data-id]'],
  ['/kalender.html', '#calendar-grid'],
  ['/verlauf.html', '#tl-body'],
  ['/analyse.html', '#an-milestones'],
  ['/standorte.html', '#place-list'],
];
for (const [url, selector] of pages) {
  await page.goto(BASE + url);
  const ok = await page.waitForSelector(selector, { timeout: 10000 }).then(() => true, () => false);
  check(ok && await page.locator('.demo-banner').count() === 1, `${url}: content and banner shown`);
}
await page.goto(BASE + '/bewerbungen.html');
await page.waitForSelector('tr[data-id]');
check(await page.locator('tr[data-id]').count() === 12, '12 fictional applications');

// ---------- Restrictions (API from the logged-in page) ----------
const blocked = await page.evaluate(async () => {
  const { token } = await (await fetch('/api/auth/antiforgery')).json();
  const [first] = await (await fetch('/api/applications')).json();
  const form = new FormData();
  form.append('file', new Blob(['%PDF-1.4\n%%EOF\n'], { type: 'application/pdf' }), 'eigene.pdf');
  form.append('category', 'Lebenslauf');
  const upload = await fetch(`/api/applications/${first.id}/documents`, { method: 'POST', headers: { 'X-CSRF-TOKEN': token }, body: form });
  const geocode = await fetch('/api/geocode', { method: 'POST', headers: { 'X-CSRF-TOKEN': token, 'Content-Type': 'application/json' }, body: JSON.stringify({ query: 'Hamburg' }) });
  const deleted = await fetch(`/api/applications/${first.id}`, { method: 'DELETE', headers: { 'X-CSRF-TOKEN': token } });
  return { upload: upload.status, geocode: geocode.status, deleted: deleted.status };
});
check(blocked.upload === 403, 'upload refused in the demo (403)');
check(blocked.geocode === 403, 'place search refused in the demo (403)');
check(blocked.deleted === 204, 'demo data itself can be changed (delete → 204)');

// ---------- A second visitor is isolated ----------
const second = await startDemo();
await second.page.goto(BASE + '/bewerbungen.html');
await second.page.waitForSelector('tr[data-id]');
check(await second.page.locator('tr[data-id]').count() === 12, 'second visitor: own complete data set (first visitor\'s delete not visible)');
await page.reload();
await page.waitForSelector('tr[data-id]');
check(await page.locator('tr[data-id]').count() === 11, 'first visitor still sees own change (11 applications)');

// ---------- Logout ----------
await page.click('[data-logout]');
await page.waitForURL(/\/willkommen\.html/);
check(true, 'logout leads back to the start page');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
await browser.close();
