// Checks the PUBLIC demo (docs/deployment.md, "After the first deployment"). Not part of run.mjs.
//
//   node public-check.mjs                    → https://bewerbungspilot.fly.dev, phase "main"
//   node public-check.mjs after-restart      → after "fly machine restart": same login, same data
//   node public-check.mjs rate-limit         → starts demos until the per-IP limit answers 429
//
// Uses at most 3 demo starts in phase "main" (limit: 5 per IP and hour). Writes the cookies of
// visitor A to .data/public-state.json (git-ignored) for the restart check.
import fs from 'node:fs';
import path from 'node:path';
import { chromium } from 'playwright';

const BASE = process.env.BASE ?? 'https://bewerbungspilot.fly.dev';
const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const STATE = path.join(DIR, '.data', 'public-state.json');
const phase = process.argv[2] ?? 'main';
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

// A visitor without a browser: cookies and antiforgery token handled by hand.
function visitor(initialCookies = {}) {
  const jar = new Map(Object.entries(initialCookies));
  let token = null;
  const v = {
    jar,
    setCookies: [],
    async call(method, url, body) {
      const response = await fetch(BASE + url, {
        method, redirect: 'manual',
        headers: {
          ...(jar.size ? { Cookie: [...jar].map(([k, val]) => `${k}=${val}`).join('; ') } : {}),
          ...(token && method !== 'GET' ? { 'X-CSRF-TOKEN': token } : {}),
          ...(body ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body ? JSON.stringify(body) : undefined,
      });
      v.setCookies = response.headers.getSetCookie();
      for (const cookie of v.setCookies) {
        const [name, ...rest] = cookie.split(';')[0].split('=');
        const value = rest.join('=');
        if (value) jar.set(name, value); else jar.delete(name);
      }
      return response;
    },
    async refresh() { token = (await (await v.call('GET', '/api/auth/antiforgery')).json()).token; },
    async json(url) { return (await v.call('GET', url)).json(); },
    async startDemo() { await v.refresh(); const r = await v.call('POST', '/api/demo/start'); await v.refresh(); return r; },
  };
  return v;
}

if (phase === 'main') {
  // ---------- HTTPS, headers, anonymous access ----------
  const http = await fetch(BASE.replace('https:', 'http:') + '/', { redirect: 'manual' });
  check([301, 302, 307, 308].includes(http.status) && http.headers.get('location')?.startsWith('https://'),
    `http:// redirects to https:// (${http.status})`);
  const health = await fetch(BASE + '/health');
  check(health.status === 200 && (await health.text()) === 'Healthy', '/health → Healthy');
  const landing = await fetch(BASE + '/willkommen.html');
  check(landing.status === 200 && (await landing.text()).includes('Demo starten'), 'start page loads');
  check(landing.headers.get('strict-transport-security')?.includes('max-age='), 'HSTS header');
  check(landing.headers.get('content-security-policy')?.includes("default-src 'self'"), 'Content-Security-Policy header');
  const foreignHost = await fetch(BASE + '/api/applications', { redirect: 'manual' });
  check(foreignHost.status === 401, `API without login → 401 (${foreignHost.status})`);
  for (const url of ['/storage/bewerbungspilot.db', '/bewerbungspilot.db', '/data/demo/bewerbungspilot.db', '/documents/', '/keys/', '/.env', '/appsettings.json'])
    check((await fetch(BASE + url)).status !== 200, `${url} not reachable`);

  // ---------- Demo accounts: two visitors, isolated ----------
  const a = visitor();
  await a.refresh();
  check([...a.jar.keys()].includes('__Host-BewerbungsPilot.Antiforgery'), 'antiforgery cookie uses the __Host- prefix');
  const startA = await a.call('POST', '/api/demo/start');
  const loginCookie = a.setCookies.find((c) => c.startsWith('__Host-BewerbungsPilot='));
  check(startA.status === 200, `visitor A: demo start → ${startA.status}`);
  check(/secure/i.test(loginCookie ?? '') && /httponly/i.test(loginCookie) && /samesite=strict/i.test(loginCookie),
    'login cookie: __Host-, Secure, HttpOnly, SameSite=Strict');
  await a.refresh();
  const meA = await a.json('/api/auth/me');
  check(meA.isDemo === true && meA.email.endsWith('@demo.invalid'), 'visitor A is a demo account');
  const appsA = await a.json('/api/applications');
  check(appsA.length === 12, `12 fictional applications (${appsA.length})`);
  check(new Set(appsA.map((x) => x.status)).size === 6, 'all six statuses present');
  const dashboard = await a.json('/api/dashboard');
  check(dashboard.total === 12, `dashboard statistics (total ${dashboard.total})`);
  const upcoming = await a.json('/api/calendar/upcoming');
  check(Array.isArray(upcoming) && upcoming.length > 0, `upcoming interviews and deadlines (${upcoming.length})`);

  // A change by A stays with A
  const victim = appsA[0];
  check((await a.call('DELETE', `/api/applications/${victim.id}`)).status === 204, 'visitor A can change own demo data');

  const b = visitor();
  check((await b.startDemo()).status === 200, 'visitor B: demo start');
  const meB = await b.json('/api/auth/me');
  check(meB.id !== meA.id, 'visitor B gets a different account');
  const appsB = await b.json('/api/applications');
  check(appsB.length === 12, `visitor B has an own complete data set (${appsB.length})`);
  check(appsB.every((x) => !appsA.some((y) => y.id === x.id)), 'no shared records between A and B');
  check((await b.call('GET', `/api/applications/${appsA[1].id}`)).status === 404, "B cannot open A's application (404)");

  // ---------- Registration closed, login answers ----------
  const anonymous = visitor();
  await anonymous.refresh();
  const options = await anonymous.json('/api/auth/antiforgery');
  check(options.allowRegistration === false && options.demoEnabled === true, 'registration off, demo on');
  const register = await anonymous.call('POST', '/api/auth/register', { email: 'test@example.org', password: 'ein-sehr-sicheres-passwort' });
  check(register.status === 403, `registration by direct API request refused (${register.status})`);
  const login = await anonymous.call('POST', '/api/auth/login', { email: 'test@example.org', password: 'falsches-passwort-123' });
  const loginBody = await login.json();
  check(login.status === 400 && loginBody.title === 'E-Mail-Adresse oder Passwort ist falsch.', 'login endpoint answers (unknown account → German message)');

  fs.mkdirSync(path.dirname(STATE), { recursive: true });
  fs.writeFileSync(STATE, JSON.stringify({ cookies: Object.fromEntries(a.jar), user: meA.id, count: appsA.length - 1 }));

  // ---------- Browser: start page, demo start, all pages, map, PDF, logout ----------
  const browser = await chromium.launch({ channel: 'msedge' });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  const problems = [];
  page.on('console', (m) => { if (m.type() === 'error' && !/status of 401/.test(m.text())) problems.push(m.text()); });
  page.on('pageerror', (e) => problems.push(e.message));

  await page.goto(BASE + '/');
  await page.waitForURL(/\/willkommen\.html/);
  await page.waitForSelector('body:not([hidden])');
  check(true, 'browser: "/" opens the start page');
  await page.goto(BASE + '/login.html');
  await page.waitForSelector('#home-link:not([hidden])'); // shown once the page knows demo mode is on
  check(await page.isVisible('#login-form') && await page.isVisible('#home-link') && await page.isHidden('#register-link'), 'browser: login page (no registration link)');
  await page.goto(BASE + '/registrieren.html');
  await page.waitForSelector('#auth-notice:not(:empty)'); // no waitForFunction: it needs eval(), which the CSP forbids
  check((await page.textContent('#auth-notice')).includes('Registrierung ist deaktiviert'), 'browser: registration page says it is disabled');

  await page.goto(BASE + '/willkommen.html');
  await page.waitForSelector('body:not([hidden])');
  await page.click('#demo-start');
  await page.waitForURL((url) => url.pathname === '/');
  await page.waitForSelector('#kpis .kpi');
  check(await page.isVisible('.demo-banner'), 'browser: demo started, dashboard with banner');
  await page.waitForSelector('#status-legend li, #status-legend > *');
  const charts = await page.evaluate(() => ['activity-chart', 'status-chart'].map((id) => document.getElementById(id)?.getBoundingClientRect().height ?? 0));
  check(charts.every((h) => h > 0), `browser: dashboard charts rendered (${charts.map(Math.round).join(', ')} px)`);

  for (const [url, selector] of [['/bewerbungen.html', 'tr[data-id]'], ['/pipeline.html', '#board [data-id]'], ['/kalender.html', '#calendar-grid'],
    ['/verlauf.html', '#tl-body .tl-row'], ['/analyse.html', '#an-milestones'], ['/standorte.html', '#place-list .place-item']]) {
    await page.goto(BASE + url);
    check(await page.waitForSelector(selector, { timeout: 15000 }).then(() => true, () => false), `browser: ${url} shows demo data`);
  }
  const tiles = await page.waitForSelector('.leaflet-tile-loaded', { timeout: 15000 }).then(() => true, () => false);
  check(tiles, 'browser: OpenStreetMap tiles load on the map');
  check(await page.locator('.leaflet-marker-icon').count() > 0, 'browser: employer markers on the map');

  await page.goto(BASE + '/bewerbungen.html');
  await page.waitForSelector('tr[data-id]');
  const withDoc = await page.evaluate(async () => {
    for (const a of await (await fetch('/api/applications')).json())
      if ((await (await fetch(`/api/applications/${a.id}/documents`)).json()).length) return a.id;
    return null;
  });
  await page.click(`tr[data-id="${withDoc}"]`);
  await page.click('#tab-btn-documents');
  await page.locator('#tab-documents [data-preview]').first().click();
  await page.waitForSelector('[data-preview-frame]:not([hidden])', { timeout: 15000 }).catch(() => {});
  check(await page.isVisible('[data-preview-frame]'), 'browser: example PDF opens in the preview');
  await page.keyboard.press('Escape');
  await page.waitForSelector('.preview-dialog', { state: 'hidden' });
  await page.keyboard.press('Escape');
  await page.waitForSelector('.side-panel[open]', { state: 'detached' }).catch(() => {});

  await page.click('[data-logout]');
  await page.waitForURL(/\/willkommen\.html/);
  check(await page.evaluate(async () => (await fetch('/api/applications')).status) === 401, 'browser: logout ends the session');
  check(problems.length === 0, `browser: no console errors or CSP violations ${problems.join(' | ')}`);
  await browser.close();
}

if (phase === 'after-restart') {
  const state = JSON.parse(fs.readFileSync(STATE, 'utf8'));
  const a = visitor(state.cookies);
  const me = await a.call('GET', '/api/auth/me');
  check(me.status === 200 && (await me.json()).id === state.user, 'after restart: same demo login still valid');
  const apps = await a.json('/api/applications');
  check(apps.length === state.count, `after restart: visitor's data incl. own change still there (${apps.length})`);
}

if (phase === 'rate-limit') {
  const statuses = [];
  for (let i = 0; i < 6; i++) {
    const r = await visitor().startDemo();
    statuses.push(r.status);
    if (r.status === 429) {
      check((await r.json()).title.includes('zu viele Demos'), 'German message for too many demo starts');
      break;
    }
  }
  check(statuses.at(-1) === 429, `demo starts from this IP are limited (${statuses.join(', ')})`);
}
