// M9 browser suite: registration/login/logout, back navigation after logout, CSRF from another
// site, cross-user isolation on every page, document access and switching accounts.
import { chromium } from 'playwright';
import http from 'node:http';
import crypto from 'node:crypto';
import { createSession, BASE, PASSWORD } from './session.mjs';

const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };
const VIEW = { viewport: { width: 1440, height: 900 } };
const PAGES = ['/', '/bewerbungen.html', '/pipeline.html', '/kalender.html', '/verlauf.html', '/analyse.html', '/standorte.html'];
const SECRETS = ['Nordlicht', 'Geheime Notiz A', 'Geheimer Ort A', 'Kontakt A'];
const EMPTY_PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');
const iso = (d) => d.toISOString().slice(0, 10);
const inDays = (n) => { const d = new Date(); d.setDate(d.getDate() + n); return d; };

const errors = [];
const browser = await chromium.launch({ channel: 'msedge' });

async function newContext(options = {}) {
  const context = await browser.newContext({ ...VIEW, ...options });
  await context.route('https://tile.openstreetmap.org/**', (route) => route.fulfill({ status: 200, contentType: 'image/png', body: EMPTY_PNG }));
  return context;
}
function watch(page) {
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|401|403|404|409)/.test(m.text())) errors.push(m.text()); });
  return page;
}
async function loginViaForm(page, email, password = PASSWORD) {
  await page.waitForSelector('#login-form');
  await page.fill('#a-email', email);
  await page.fill('#a-password', password);
  await page.click('#login-form button[type="submit"]');
}
async function visibleText(page) {
  await page.waitForLoadState('networkidle');
  return page.locator('body').innerText();
}
const pdf = Buffer.from(`%PDF-1.4\n% Geheimes Dokument A ${crypto.randomUUID()}\n`);
const sha = (buffer) => crypto.createHash('sha256').update(buffer).digest('hex');

// ---------- 1. Registration and login through the forms ----------
const ctx1 = await newContext();
const page = watch(await ctx1.newPage());
await page.goto(BASE + '/bewerbungen.html');
await page.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
check(page.url().includes('returnUrl=%2Fbewerbungen.html'), 'not logged in → login page with returnUrl');
check((await page.textContent('#auth-notice')).trim() === '', 'first visit: no "session expired" message');
await page.waitForSelector('#register-link:not([hidden])');
check(true, 'registration link shown (registration enabled)');

await page.click('#login-form button[type="submit"]');
check((await page.textContent('#a-email-error')).includes('E-Mail-Adresse'), 'empty login form → German field error');

await page.click('#register-link a');
await page.waitForURL(/registrieren\.html/, { waitUntil: 'commit' });
await page.fill('#a-email', 'keine-adresse');
await page.fill('#a-password', PASSWORD);
await page.click('#register-form button[type="submit"]');
await page.waitForSelector('#a-email-error:has-text("gültige E-Mail")');
check(true, 'registration: invalid e-mail → German message');
await page.fill('#a-email', 'anna@example.org');
await page.fill('#a-password', 'kurz');
await page.click('#register-form button[type="submit"]');
await page.waitForSelector('#a-password-error:has-text("mindestens 12 Zeichen")');
check(true, 'registration: short password → German message');
await page.fill('#a-password', PASSWORD);
await page.click('#register-form button[type="submit"]');
await page.waitForURL(BASE + '/', { waitUntil: 'commit' });
await page.waitForSelector('[data-user-email]:has-text("anna@example.org")');
check(true, 'registration logs in and shows the account in the sidebar');

await page.click('[data-logout]');
await page.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
check(true, 'logout → login page');
check((await (await ctx1.request.get(BASE + '/api/auth/me')).status()) === 401, 'after logout the API answers 401');

await loginViaForm(page, 'anna@example.org', 'falsches-passwort-123');
await page.waitForSelector('#login-form [data-form-alert]:has-text("E-Mail-Adresse oder Passwort ist falsch")');
check(true, 'wrong password → generic German message');
check(!(await page.inputValue('#a-password')), 'password field cleared after failure');

await page.goto(BASE + '/login.html?returnUrl=//evil.example.org/steal');
await loginViaForm(page, 'anna@example.org');
await page.waitForURL(BASE + '/', { waitUntil: 'commit' });
check(new URL(page.url()).host === 'localhost:5299', 'returnUrl to another site is ignored (open redirect blocked)');

await page.click('[data-logout]');
await page.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
await page.goto(BASE + '/kalender.html');
await page.waitForURL(/\/login\.html\?returnUrl=%2Fkalender\.html/, { waitUntil: 'commit' });
await loginViaForm(page, 'anna@example.org');
await page.waitForURL(BASE + '/kalender.html', { waitUntil: 'commit' });
check(true, 'login returns to the requested page (returnUrl)');
await page.goto(BASE + '/login.html');
await page.waitForURL(BASE + '/', { waitUntil: 'commit' });
check(true, 'login page while logged in → straight to the app');
await ctx1.close();

// ---------- 2. Data of A (Anna) and B (Ben) ----------
const A = await createSession({ email: 'anna@example.org', register: false });
const B = await createSession({ email: 'ben@example.org' });
const json = (r) => r.json();
const send = (s, method, url, body) => s.fetch(BASE + url, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
const appA = await send(A, 'POST', '/api/applications', {
  companyName: 'Nordlicht GmbH', position: 'Auszubildender FIAE', location: 'Hamburg', status: 'Beworben',
  appliedOn: iso(inDays(-5)), deadline: iso(inDays(9)), notes: 'Geheime Notiz A',
}).then(json);
await send(A, 'PUT', `/api/applications/${appA.id}/map-location`, { precision: 'Exact', latitude: 53.5446, longitude: 9.9661, label: 'Geheimer Ort A' });
const interviewA = await send(A, 'POST', `/api/applications/${appA.id}/interviews`, {
  scheduledAt: `${iso(inDays(6))}T14:30:00`, type: 'Video', contactPerson: 'Kontakt A', status: 'Geplant',
}).then(json);
const form = new FormData();
form.append('file', new Blob([pdf], { type: 'application/pdf' }), 'Lebenslauf_Anna.pdf');
form.append('category', 'Lebenslauf');
const docA = await A.fetch(`${BASE}/api/applications/${appA.id}/documents`, { method: 'POST', body: form }).then(json);
const appB = await send(B, 'POST', '/api/applications', { companyName: 'Hafen Logistik AG', position: 'Fachinformatiker', status: 'Beworben', appliedOn: iso(inDays(-2)) }).then(json);
check(appA.id && interviewA.id && docA.id && appB.id, 'seed data for Anna and Ben created');

// ---------- 3. Cross-user isolation on every page ----------
const ctxB = await newContext();
await ctxB.addCookies([...B.cookies()].map(([name, value]) => ({ name, value, url: BASE })));
const pageB = watch(await ctxB.newPage());
for (const path of PAGES) {
  await pageB.goto(BASE + path);
  const text = await visibleText(pageB);
  const html = await pageB.content();
  check(SECRETS.every((s) => !text.includes(s) && !html.includes(s)), `Ben on ${path}: no data of Anna`);
}
await pageB.goto(BASE + '/bewerbungen.html');
await pageB.waitForSelector('tr[data-id]');
check((await pageB.locator('tr[data-id]').count()) === 1 && (await visibleText(pageB)).includes('Hafen Logistik AG'), 'Ben sees exactly his own application');
await pageB.goto(`${BASE}/bewerbungen.html?id=${appA.id}`);
await pageB.waitForSelector('.toast');
check((await pageB.textContent('.toast')).includes('nicht gefunden') && !(await pageB.content()).includes('Nordlicht'), "Ben opening Anna's id via URL → not found");

// ---------- 4. Document access ----------
for (const [method, url] of [['GET', `/api/documents/${docA.id}/preview`], ['HEAD', `/api/documents/${docA.id}/preview`], ['GET', `/api/documents/${docA.id}/download`], ['GET', `/api/applications/${appA.id}/documents`]]) {
  const r = await ctxB.request.fetch(BASE + url, { method });
  const body = await r.body();
  check(r.status() === 404 && !body.toString('latin1').startsWith('%PDF') && !body.toString().includes('Lebenslauf_Anna'), `Ben ${method} ${url.replace(String(docA.id), '<A>').replace(String(appA.id), '<A>')} → 404, no PDF`);
}
const anonymous = await globalThis.fetch(`${BASE}/api/documents/${docA.id}/download`);
check(anonymous.status === 401, 'anonymous document download → 401');
const formB = new FormData();
formB.append('file', new Blob([pdf], { type: 'application/pdf' }), 'fremd.pdf');
formB.append('category', 'Sonstiges');
check((await B.fetch(`${BASE}/api/applications/${appA.id}/documents`, { method: 'POST', body: formB })).status === 404, "Ben uploading to Anna's application → 404");
check((await send(B, 'DELETE', `/api/documents/${docA.id}`)).status === 404, "Ben deleting Anna's document → 404");
const docsA = await A.fetch(`${BASE}/api/applications/${appA.id}/documents`).then(json);
check(docsA.length === 1, "Anna's documents unchanged after Ben's attempts");

const ctxA = await A.newContext(browser, VIEW);
const pageA = watch(await ctxA.newPage());
await pageA.goto(`${BASE}/bewerbungen.html?id=${appA.id}`);
await pageA.waitForSelector('dialog.side-panel[open]');
await pageA.click('#tab-btn-documents');
const docRow = pageA.locator('.document-row', { hasText: 'Lebenslauf_Anna.pdf' });
await docRow.locator('[data-preview]').first().click();
await pageA.waitForSelector('[data-preview-frame]:not([hidden])', { timeout: 8000 }).catch(() => {});
check(await pageA.isVisible('[data-preview-frame]'), 'Anna: PDF preview loads in the iframe');
await pageA.keyboard.press('Escape');
await pageA.waitForSelector('.preview-dialog', { state: 'hidden' });
const [download] = await Promise.all([pageA.waitForEvent('download'), docRow.locator('a[download]').click()]);
const downloaded = await download.path().then((p) => import('node:fs').then((fs) => fs.readFileSync(p)));
check(sha(downloaded) === sha(pdf), 'Anna: download is byte-identical (SHA-256)');

// ---------- 5. Caching headers ----------
for (const url of ['/bewerbungen.html', '/', `/api/applications/${appA.id}`, '/api/dashboard', `/api/documents/${docA.id}/preview`]) {
  const r = await ctxA.request.get(BASE + url);
  check((r.headers()['cache-control'] ?? '').includes('no-store'), `Cache-Control: no-store on ${url.replace(String(appA.id), '<id>').replace(String(docA.id), '<id>')}`);
}

// ---------- 6. Logout and the Back button ----------
await pageA.goto(`${BASE}/bewerbungen.html`);
await pageA.waitForSelector('tr[data-id]');
await pageA.goto(`${BASE}/bewerbungen.html?id=${appA.id}`);
await pageA.waitForSelector('dialog.side-panel[open]');
check((await visibleText(pageA)).includes('Geheime Notiz A'), 'before logout: Anna sees her details');
await pageA.keyboard.press('Escape'); // the detail panel is modal; Back below reopens it via ?id=
await pageA.waitForSelector('dialog.side-panel[open]', { state: 'detached' }).catch(() => {});
await pageA.click('[data-logout]');
await pageA.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
for (let i = 1; i <= 3; i++) {
  await pageA.goBack().catch(() => {});
  await pageA.waitForTimeout(1200);
  const text = await pageA.locator('body').innerText().catch(() => '');
  check(SECRETS.every((s) => !text.includes(s)), `Back #${i} after logout: no private data visible (${new URL(pageA.url()).pathname})`);
}
await pageA.waitForURL(/\/login\.html/, { timeout: 5000 }).catch(() => {});
check(pageA.url().includes('/login.html'), 'Back after logout ends on the login page');
check((await (await ctxA.request.get(`${BASE}/api/applications/${appA.id}`)).status()) === 401, 'old session cookie no longer accepted');
// ctxA stays open until browser.close(): closing a context right after a download trips Playwright.
await pageA.goto('about:blank');

// ---------- 7. CSRF from another website ----------
const A2 = await createSession({ email: 'anna@example.org', register: false }); // logout above ended all of Anna's sessions
const attackerHits = [];
const attacker = http.createServer((req, res) => {
  attackerHits.push(req.url);
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(`<!doctype html><title>Gewinnspiel</title>
    <form id="upload" method="POST" enctype="multipart/form-data" action="${BASE}/api/applications/${appA.id}/documents">
      <input type="hidden" name="category" value="Sonstiges"></form>
    <form id="json" method="POST" enctype="text/plain" action="${BASE}/api/applications">
      <input name='{"companyName":"CSRF AG","position":"x","status":"Entwurf","x":"' value='"}'></form>
    <script>
      fetch('${BASE}/api/applications', { method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ companyName: 'CSRF Fetch AG', position: 'x', status: 'Entwurf' }) })
        .then((r) => r.text()).then((t) => { document.title = 'gelesen:' + t; }).catch(() => { document.title = 'blockiert'; });
      fetch('${BASE}/api/applications/${appA.id}/documents', { method: 'POST', mode: 'no-cors', credentials: 'include', body: new FormData(document.getElementById('upload')) });
      fetch('${BASE}/api/applications', { credentials: 'include' }).then((r) => r.text()).then((t) => { document.body.dataset.read = t; }).catch(() => {});
      setTimeout(() => document.getElementById('${'upload'}').submit(), 800);
    </script>`);
}).listen(5398);

const ctxCsrf = await A2.newContext(browser, VIEW);
const evil = watch(await ctxCsrf.newPage());
const formResponses = [];
evil.on('response', (r) => { if (r.url().startsWith(BASE + '/api/')) formResponses.push(`${r.request().method()} ${r.status()}`); });
evil.removeAllListeners('console');
await evil.goto('http://localhost:5398/');
await evil.waitForTimeout(2500);
check(formResponses.some((r) => r === 'POST 403'), `cross-site form POST (cookie sent, same site) → 403 by the CSRF check (${formResponses.join(', ')})`);
const appsAfter = await A2.fetch(`${BASE}/api/applications`).then(json);
check(!appsAfter.some((a) => a.companyName.includes('CSRF')), 'no application created by the attacker page');
check((await A2.fetch(`${BASE}/api/applications/${appA.id}/documents`).then(json)).length === 1, 'no document uploaded by the attacker page');
const title = await evil.title().catch(() => '');
check(!title.startsWith('gelesen:') && !(await evil.evaluate(() => document.body.dataset.read ?? '')).includes('Nordlicht'), 'attacker page cannot read any API response');

// Missing / stale token from the logged-in page itself.
const own = watch(await ctxCsrf.newPage());
await own.goto(BASE + '/bewerbungen.html');
await own.waitForSelector('tr[data-id]');
const statuses = await own.evaluate(async () => {
  const body = JSON.stringify({ companyName: 'Ohne Token AG', position: 'x', status: 'Entwurf' });
  const none = await fetch('/api/applications', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body });
  const stale = await fetch('/api/applications', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': 'CfDJ8-veraltet' }, body });
  return [none.status, stale.status];
});
check(statuses[0] === 403 && statuses[1] === 403, `same-site request without / with stale token → 403 (${statuses})`);
check(!(await A2.fetch(`${BASE}/api/applications`).then(json)).some((a) => a.companyName === 'Ohne Token AG'), 'nothing created without a valid token');
attacker.close();
await evil.goto('about:blank');
await own.goto('about:blank');
await ctxCsrf.close();

// ---------- 8. Switching accounts in one browser ----------
const ctxS = await newContext();
const tab = watch(await ctxS.newPage());
await tab.goto(BASE + '/standorte.html');
await loginViaForm(tab, 'anna@example.org');
await tab.waitForURL(BASE + '/standorte.html', { waitUntil: 'commit' });
const annaId = A2.user.id;
await tab.evaluate((key) => localStorage.setItem(key, JSON.stringify({ latitude: 53.55, longitude: 9.99, label: 'Annas Startpunkt' })), `bewerbungspilot.origin.${annaId}`);
await tab.reload();
await tab.waitForSelector('#origin-bar:has-text("Annas Startpunkt")');
check(true, "Anna's starting point is shown for Anna");
await tab.click('[data-logout]');
await tab.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
check(await tab.evaluate((key) => localStorage.getItem(key), `bewerbungspilot.origin.${annaId}`) === null, "logout removes Anna's starting point from the browser");
await loginViaForm(tab, 'ben@example.org');
await tab.waitForURL(BASE + '/', { waitUntil: 'commit' });
for (const path of ['/bewerbungen.html', '/standorte.html']) {
  await tab.goto(BASE + path);
  const text = await visibleText(tab);
  check(!text.includes('Annas Startpunkt') && SECRETS.every((s) => !text.includes(s)), `after switching to Ben (${path}): nothing of Anna, no cached data or starting point`);
}
check((await visibleText(tab)).includes('Hafen Logistik AG'), 'Ben sees his own data after switching');

// Two tabs: tab 1 shows Anna; in tab 2 the user logs out and logs in as Ben.
await tab.click('[data-logout]');
await tab.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
await loginViaForm(tab, 'anna@example.org');
await tab.waitForURL(BASE + '/', { waitUntil: 'commit' });
await tab.goto(BASE + '/bewerbungen.html');
await tab.waitForSelector('tr[data-id]');
check((await visibleText(tab)).includes('Nordlicht'), 'tab 1: Anna logged in');
const tab2 = watch(await ctxS.newPage());
await tab2.goto(BASE + '/');
await tab2.waitForSelector('[data-user-email]:has-text("anna@example.org")');
await tab2.click('[data-logout]');
await tab2.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
await loginViaForm(tab2, 'ben@example.org');
await tab2.waitForURL(BASE + '/', { waitUntil: 'commit' });
await tab.waitForFunction(() => document.body.innerText.includes('Hafen Logistik AG'), null, { timeout: 8000 }).catch(() => {});
const tab1Text = await tab.locator('body').innerText();
check(!tab1Text.includes('Nordlicht') && tab1Text.includes('Hafen Logistik AG'), 'tab 1 reloaded automatically: shows Ben, nothing of Anna');

// Stale tab without the storage event (account switched through the API in tab 2): a write from
// tab 1 must never land in the other account.
await tab.click('[data-logout]');
await tab.waitForURL(/\/login\.html/, { waitUntil: 'commit' });
await loginViaForm(tab, 'anna@example.org');
await tab.waitForURL(BASE + '/', { waitUntil: 'commit' });
await tab.goto(BASE + '/bewerbungen.html');
await tab.waitForSelector('tr[data-id]');
await tab2.goto(BASE + '/'); // tab 2 reloaded itself after tab 1's login; start from a settled page
await tab2.waitForLoadState('networkidle');
await tab2.evaluate(async (password) => { // no localStorage signal → tab 1 is not told
  const token = async () => (await (await fetch('/api/auth/antiforgery')).json()).token;
  await fetch('/api/auth/logout', { method: 'POST', headers: { 'X-CSRF-TOKEN': await token() } });
  await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await token() },
    body: JSON.stringify({ email: 'ben@example.org', password }) });
}, PASSWORD);
const staleResult = await tab.evaluate(async () => {
  const { api } = await import('/js/api.js');
  try { await api.createApplication({ companyName: 'Aus altem Tab GmbH', position: 'x', status: 'Entwurf' }); return 'gespeichert'; }
  catch (error) { return `abgewiesen ${error.status}`; }
}).catch(() => 'Seite neu geladen');
const benApps = await B.fetch(`${BASE}/api/applications`).then(json).catch(() => null)
  ?? await (await createSession({ email: 'ben@example.org', register: false })).fetch(`${BASE}/api/applications`).then(json);
check(staleResult !== 'gespeichert' && !benApps.some((a) => a.companyName === 'Aus altem Tab GmbH'), `stale tab write is not saved into the other account (${staleResult})`);
await tab.waitForLoadState('networkidle');
check(!(await tab.locator('body').innerText()).includes('Nordlicht'), 'stale tab reloads and no longer shows Anna');
await tab.goto('about:blank');
await tab2.goto('about:blank');
await ctxS.close();

check(errors.length === 0, `no unexpected browser errors${errors.length ? ': ' + errors.join(' | ') : ''}`);

// Playwright 1.49 reports the already closed download target as an error while the browser shuts
// down (after all checks). Only that error is ignored, and only during shutdown.
process.on('uncaughtException', (error) => { if (error.name !== 'TargetClosedError') throw error; });
await browser.close();
