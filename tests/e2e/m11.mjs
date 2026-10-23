// M11 public demo. run.mjs starts the app with Demo:Enabled = true on a throwaway data directory.
// A visitor opens the start page, understands what the app does, starts a demo, uses every page
// (no Content-Security-Policy violations), opens a document and leaves again.
import { chromium } from 'playwright';

const BASE = 'http://localhost:5299';
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

const browser = await chromium.launch({ channel: 'msedge' });
const errors = [];
const csp = [];

function watch(page) {
  page.on('console', (m) => {
    if (/Content Security Policy|Content-Security-Policy/i.test(m.text())) csp.push(m.text());
    // 401 before login / after logout is expected.
    else if (m.type() === 'error' && !/status of 401/.test(m.text())) errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(e.message));
}

// ---------- Start page ----------
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
watch(page);
await page.goto(BASE + '/');
await page.waitForURL(/\/willkommen\.html/);
await page.waitForSelector('body:not([hidden])');
check(await page.title() === 'BewerbungsPilot – Bewerbungen im Blick behalten', 'anonymous "/" opens the start page');
check((await page.textContent('h1')).includes('Alle Bewerbungen an einem Ort'), 'headline explains the product');
check(await page.locator('.landing-features li').count() === 6, 'six feature descriptions');
check(await page.isVisible('#demo-start') && (await page.textContent('#demo-start')) === 'Demo starten', 'primary button "Demo starten"');
check(await page.locator('a[data-login-link]:has-text("Anmelden")').count() === 2, 'secondary "Anmelden" links');
check(await page.locator('a[href*="registrieren"]').count() === 0, 'no registration link');
await page.locator('.landing-gallery img').last().scrollIntoViewIfNeeded();
// page.evaluate (not waitForFunction): runs without eval(), so the strict CSP stays active here.
const loaded = await page.evaluate(() => Promise.all([...document.images].map((img) => img.decode().then(() => img.naturalWidth > 0, () => false))));
check(loaded.length >= 4 && loaded.every(Boolean), `all screenshots loaded (${loaded.length})`);
const loginHref = await page.getAttribute('a.landing-cta[data-login-link]', 'href');
check(loginHref.startsWith('/login.html?returnUrl='), `login link keeps the return address (${loginHref})`);

// Login page links back to the start page in demo mode
await page.goto(BASE + '/login.html');
await page.waitForSelector('#home-link:not([hidden])');
check(await page.isVisible('#login-form'), 'login form still available');

// Expired session notice
await page.goto(BASE + '/willkommen.html?expired=1');
await page.waitForSelector('body:not([hidden])');
check((await page.textContent('#landing-notice')).includes('Sitzung ist abgelaufen'), 'expired-session notice on the start page');

// ---------- Demo start ----------
await page.goto(BASE + '/willkommen.html');
await page.waitForSelector('body:not([hidden])');
await page.click('#demo-start');
await page.waitForURL((url) => url.pathname === '/' || url.pathname === '/index.html');
await page.waitForSelector('.demo-banner');
check(true, 'demo started, dashboard with demo banner');

// ---------- Every application page ----------
const pages = [
  ['/', '#kpis .kpi'],
  ['/bewerbungen.html', 'tr[data-id]'],
  ['/pipeline.html', '#board [data-id]'],
  ['/kalender.html', '#calendar-grid'],
  ['/verlauf.html', '#tl-body'],
  ['/analyse.html', '#an-milestones'],
  ['/standorte.html', '#place-list .place-item'],
];
for (const [url, selector] of pages) {
  await page.goto(BASE + url);
  const ok = await page.waitForSelector(selector, { timeout: 10000 }).then(() => true, () => false);
  check(ok, `${url}: loads with demo data`);
}
const tilePolicy = await page.getAttribute('.leaflet-tile-pane img', 'referrerpolicy').catch(() => null);
check(tilePolicy === 'strict-origin-when-cross-origin', `map tiles send only the origin as Referer (${tilePolicy})`);

// ---------- Document preview ----------
const withDocument = await page.evaluate(async () => {
  const list = await (await fetch('/api/applications')).json();
  for (const a of list) {
    const docs = await (await fetch(`/api/applications/${a.id}/documents`)).json();
    if (docs.length) return a.id;
  }
  return null;
});
check(withDocument !== null, 'demo contains an application with a document');
await page.goto(BASE + '/bewerbungen.html');
await page.click(`tr[data-id="${withDocument}"]`);
await page.click('#tab-btn-documents');
await page.locator('#tab-documents [data-preview]').first().click();
await page.waitForSelector('.preview-dialog[open]');
await page.waitForSelector('[data-preview-frame]:not([hidden])', { timeout: 8000 }).catch(() => {});
check(await page.isVisible('[data-preview-frame]'), 'PDF preview opens');
await page.keyboard.press('Escape');
await page.waitForSelector('.preview-dialog', { state: 'hidden' });
await page.keyboard.press('Escape'); // closes the detail panel
await page.waitForSelector('.side-panel[open]', { state: 'detached' }).catch(() => {});

// ---------- Mobile ----------
const mobile = await browser.newContext({ viewport: { width: 375, height: 800 } });
const phone = await mobile.newPage();
watch(phone);
await phone.goto(BASE + '/willkommen.html');
await phone.waitForSelector('body:not([hidden])');
const overflow = await phone.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
check(overflow <= 0, `start page: no horizontal scroll at 375px (${overflow}px)`);
check(await phone.isVisible('#demo-start'), 'start page: demo button visible on mobile');
await mobile.close();

// ---------- Leaving ----------
await page.click('[data-logout]');
await page.waitForURL(/\/willkommen\.html/);
await page.waitForSelector('body:not([hidden])');
check(true, 'logout returns to the start page');
const after = await page.evaluate(async () => (await fetch('/api/applications')).status);
check(after === 401, `demo data no longer reachable after logout (${after})`);

check(csp.length === 0, `no Content-Security-Policy violations ${csp.join(' | ')}`);
check(errors.length === 0, `no console errors ${errors.join(' | ')}`);
await browser.close();
