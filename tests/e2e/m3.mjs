import { chromium } from 'playwright';
import { createSession } from './session.mjs';

const BASE = 'http://localhost:5299';
// M9: the suite works as its own registered user; fetch sends the login cookie and CSRF token.
const session = await createSession();
const fetch = session.fetch;
const SHOTS = new URL('./shots/', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };
const json = (r) => r.json();
const post = (body) => fetch(BASE + '/api/applications', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).then(json);
const getApp = (id) => fetch(`${BASE}/api/applications/${id}`).then(json);

// Seed isolated test data through the real API
const nord = await post({ companyName: 'Nordlicht Software GmbH', position: 'Auszubildender FIAE', location: 'Hamburg', status: 'Beworben', appliedOn: '2026-10-01', notes: 'Bitte nicht verlieren' });
const mueller = await post({ companyName: 'Müller Logistik AG', position: 'Fachinformatiker AE', location: 'Bremen', status: 'InPruefung', appliedOn: '2026-09-10' });
const kiel = await post({ companyName: 'Stadtwerke Kiel', position: 'Ausbildung Anwendungsentwicklung', location: 'Kiel', status: 'Entwurf' });
await post({ companyName: 'Hanse Data KG', position: 'Azubi Softwareentwicklung', location: 'Lübeck', status: 'Absage', appliedOn: '2026-08-02' });

const browser = await chromium.launch({ channel: 'msedge' });
const page = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 } })).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/status of 500/.test(m.text())) errors.push(m.text()); });

const column = (status) => page.locator(`.column-list[data-status="${status}"]`);
const card = (id) => page.locator(`.kanban-card[data-id="${id}"]`);

async function drag(id, toStatus, screenshot) {
  const from = await card(id).boundingBox();
  const to = await column(toStatus).boundingBox();
  await page.mouse.move(from.x + from.width / 2, from.y + 20);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 10, from.y + 25, { steps: 3 });
  await page.mouse.move(to.x + to.width / 2, to.y + 60, { steps: 15 });
  await page.waitForTimeout(150);
  if (screenshot) await page.screenshot({ path: SHOTS + screenshot });
  const highlighted = await page.locator(`.kanban-column[data-status="${toStatus}"].is-over`).count();
  await page.mouse.up();
  return highlighted;
}

// 1–2. Load, correct columns, counts, empty state
await page.goto(BASE + '/pipeline.html');
await page.waitForSelector('.kanban-card');
check(await column('Beworben').locator(`[data-id="${nord.id}"]`).count() === 1, 'Nordlicht in "Beworben"');
check(await column('InPruefung').locator(`[data-id="${mueller.id}"]`).count() === 1, 'Müller in "In Prüfung"');
check(await column('Entwurf').locator(`[data-id="${kiel.id}"]`).count() === 1, 'Kiel in "Entwurf"');
check(await page.locator('.kanban-column[data-status="Angebot"] [data-empty]').isVisible(), 'empty column shows empty state');
check(await page.textContent('.kanban-column[data-status="Absage"] [data-count]') === '1', 'Absage counter = 1');
await page.screenshot({ path: SHOTS + 'm3-01-board.png' });

// 3–5. Drag Nordlicht: Beworben → In Prüfung
const highlighted = await drag(nord.id, 'InPruefung', 'm3-02-dragging.png');
check(highlighted === 1, 'destination column highlighted during drag');
await page.waitForSelector('.toast:has-text("Beworben → In Prüfung")');
check(await column('InPruefung').locator(`[data-id="${nord.id}"]`).count() === 1, 'card now in "In Prüfung"');
check(await page.textContent('.kanban-column[data-status="InPruefung"] [data-count]') === '2', 'In Prüfung counter updated to 2');
check(await page.textContent('.kanban-column[data-status="Beworben"] [data-count]') === '0', 'Beworben counter updated to 0');
await page.waitForTimeout(300);
await page.screenshot({ path: SHOTS + 'm3-03-dropped.png' });
let server = await getApp(nord.id);
check(server.application.status === 'InPruefung', 'server status = InPruefung');
check(server.events.filter((e) => e.type === 'StatusChanged').length === 1, 'exactly one StatusChanged event');
check(server.application.notes === 'Bitte nicht verlieren' && server.application.location === 'Hamburg', 'unrelated fields preserved');

await page.reload();
await page.waitForSelector('.kanban-card');
check(await column('InPruefung').locator(`[data-id="${nord.id}"]`).count() === 1, 'move persists after reload');

// 8. Click card opens details with history; dropdown click does not open details
await card(nord.id).locator('.card-position').click();
await page.waitForSelector('dialog.side-panel[open]');
await page.click('#tab-btn-history');
check(await page.locator('.timeline-text:has-text("Status geändert: Beworben → In Prüfung")').count() === 1, 'detail panel history shows the move');
await page.keyboard.press('Escape');
await page.waitForSelector('dialog.side-panel', { state: 'hidden' });
await card(kiel.id).locator('.status-select').click();
await page.keyboard.press('Escape');
await page.waitForTimeout(300);
check(!(await page.locator('dialog.side-panel[open]').count()), 'clicking the dropdown does not open details');

// 6. Failed update restores the card
await page.route('**/api/applications/*/status', (route) =>
  route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Serverfehler (Test).' }) }));
await drag(mueller.id, 'Angebot');
await page.waitForSelector('.toast-error');
check((await page.textContent('.toast-error')).includes('Status konnte nicht gespeichert werden'), 'German error toast on failure');
check(await column('InPruefung').locator(`[data-id="${mueller.id}"]`).count() === 1, 'card restored to original column');
check(await page.textContent('.kanban-column[data-status="Angebot"] [data-count]') === '0', 'Angebot counter unchanged');
check((await getApp(mueller.id)).application.status === 'InPruefung', 'server unchanged after failure');
await page.unroute('**/api/applications/*/status');

// 7. Keyboard-only status change via dropdown
await card(kiel.id).locator('.status-select').focus();
await page.keyboard.press('ArrowDown'); // Entwurf → Beworben (closed <select> changes on arrow keys)
await page.waitForSelector(`.column-list[data-status="Beworben"] [data-id="${kiel.id}"]`);
check(true, 'keyboard dropdown moved card to "Beworben"');
check(await page.evaluate((id) => document.activeElement?.id === `status-${id}`, kiel.id), 'focus stays on the moved card\'s dropdown');
server = await getApp(kiel.id);
check(server.application.status === 'Beworben' && server.application.appliedOn !== null, 'server: Beworben + appliedOn filled');

// Search
await page.fill('#pipeline-search', 'bremen');
check(await page.locator('.kanban-card:not([hidden])').count() === 1, 'search "bremen" → 1 visible card');
check(await page.textContent('.kanban-column[data-status="InPruefung"] [data-count]') === '1/2', 'counter shows filtered/total');
await page.fill('#pipeline-search', '');

// Dashboard reflects changes
const dash = await fetch(BASE + '/api/dashboard').then(json);
check(dash.inProgress === 3 && dash.drafts === 0 && dash.rejections === 1, `dashboard: inProgress=${dash.inProgress}, drafts=${dash.drafts}, rejections=${dash.rejections}`);

// Existing pages still load
for (const path of ['/', '/bewerbungen.html']) {
  await page.goto(BASE + path);
  await page.waitForLoadState('networkidle');
}
check(true, 'dashboard and list pages load');

// 9. Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.goto(BASE + '/pipeline.html');
await page.waitForSelector('.switcher-chip');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile: no page-level horizontal overflow');
check(await page.locator('.kanban-column:visible').count() === 1, 'mobile: one column visible');
await page.click('.switcher-chip[data-column="InPruefung"]');
check(await page.locator('.kanban-column[data-status="InPruefung"]').isVisible(), 'mobile: chip switches column');
await page.screenshot({ path: SHOTS + 'm3-04-mobile.png' });
await card(mueller.id).locator('.status-select').selectOption('Vorstellungsgespraech');
await page.waitForSelector('.toast:has-text("In Prüfung → Vorstellungsgespräch")');
check((await getApp(mueller.id)).application.status === 'Vorstellungsgespraech', 'mobile: dropdown change persisted');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);

// Reduced motion
const rm = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' })).newPage();
await rm.goto(BASE + '/pipeline.html');
await rm.waitForSelector('.kanban-card');
const duration = await rm.evaluate(() => getComputedStyle(document.querySelector('.kanban-card')).transitionDuration);
check(duration.split(',').every((d) => parseFloat(d) === 0), `reduced motion: card transition = ${duration}`);

await browser.close();
