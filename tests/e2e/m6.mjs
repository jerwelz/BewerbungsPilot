import { chromium } from 'playwright';
import { createSession } from './session.mjs';
import { DatabaseSync } from 'node:sqlite';
import path from 'node:path';

const BASE = 'http://localhost:5299';
// M9: the suite works as its own registered user; fetch sends the login cookie and CSRF token.
const session = await createSession();
const fetch = session.fetch;
const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const SHOTS = path.join(DIR, 'shots');
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

// ---------- dates relative to today (local) ----------
const iso = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const day = (offset, hour = 10) => { const d = new Date(); d.setHours(hour, 0, 0, 0); d.setDate(d.getDate() + offset); return d; };
const dbTime = (d) => d.toISOString().replace('T', ' ').slice(0, 19); // UTC, as EF Core stores it

// ---------- seed via API ----------
const json = (r) => r.json();
const send = (method, url, body) => fetch(BASE + url, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
const create = (company, status, appliedOffset, extra = {}) => send('POST', '/api/applications', {
  companyName: company, position: 'Auszubildender FIAE', status,
  appliedOn: appliedOffset === null ? null : iso(day(appliedOffset)), ...extra,
}).then(json);

const A = await create('Nordlicht Software GmbH', 'Vorstellungsgespraech', -40);
const B = await create('Müller Logistik AG', 'InPruefung', -25);
const C = await create('Hanse Data KG', 'Absage', -60);
const D = await create('Stadtwerke Kiel', 'Entwurf', null, { deadline: iso(day(5)) });
const E = await create('Altdaten GmbH', 'InPruefung', -50);
const F = await create('Same-Day AG', 'Vorstellungsgespraech', -8);
await send('POST', `/api/applications/${A.id}/interviews`, { scheduledAt: `${iso(day(3))}T14:00:00`, type: 'Video', status: 'Geplant' });

// ---------- backdate the recorded history in the TEST database ----------
const db = new DatabaseSync(path.join(process.env.DB_DIR, 'bewerbungspilot.db'));
function history(app, createdStatus, createdOffset, changes, { noHistory = false } = {}) {
  db.prepare(`DELETE FROM ApplicationEvents WHERE JobApplicationId = ? AND Type IN ('Created','StatusChanged')`).run(app.id);
  const insert = db.prepare(`INSERT INTO ApplicationEvents (JobApplicationId, Type, FromStatus, ToStatus, Description, OccurredAt) VALUES (?,?,?,?,?,?)`);
  const createdAt = day(createdOffset);
  if (!noHistory) insert.run(app.id, 'Created', null, createdStatus, 'Bewerbung angelegt', dbTime(createdAt));
  let last = createdAt;
  for (const [from, to, at] of changes) {
    insert.run(app.id, 'StatusChanged', from, to, `Status geändert: ${from} → ${to}`, dbTime(at));
    last = at;
  }
  db.prepare('UPDATE Applications SET CreatedAt = ?, StatusChangedAt = ? WHERE Id = ?').run(dbTime(createdAt), dbTime(last), app.id);
}
history(A, 'Entwurf', -45, [['Entwurf', 'Beworben', day(-40)], ['Beworben', 'InPruefung', day(-30, 14)], ['InPruefung', 'Vorstellungsgespraech', day(-12)]]);
history(B, 'Beworben', -25, [['Beworben', 'InPruefung', day(-20)], ['InPruefung', 'Beworben', day(-15)], ['Beworben', 'InPruefung', day(-14)]]);
history(C, 'Beworben', -60, [['Beworben', 'Absage', day(-35)]]);
history(D, 'Entwurf', -5, []);
history(E, null, -50, [], { noHistory: true }); // legacy data: no recorded history at all
history(F, 'Beworben', -8, [['Beworben', 'InPruefung', day(-8, 11)], ['InPruefung', 'Vorstellungsgespraech', day(-8, 12)]]);
db.close();

// ---------- API checks ----------
let r = await fetch(`${BASE}/api/timeline?from=${iso(day(0))}&to=${iso(day(-5))}`);
check(r.status === 400, `timeline with reversed range → ${r.status}`);
const timeline = await fetch(`${BASE}/api/timeline`).then(json);
const tA = timeline.applications.find((j) => j.id === A.id);
check(tA.segments.length === 4 && tA.historyComplete, 'API: Nordlicht has 4 recorded segments, complete');
check(!JSON.stringify(timeline).includes('notes') && !('location' in tA), 'API: no notes/location in timeline response');
const tE = timeline.applications.find((j) => j.id === E.id);
check(!tE.historyComplete && tE.segments.length === 1 && tE.segments[0].uncertain && tE.transitions.length === 0, 'API: legacy application → one uncertain segment, nothing invented');

// ---------- Browser ----------
const browser = await chromium.launch({ channel: 'msedge' });
const page = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 } })).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|500)/.test(m.text())) errors.push(m.text()); });

// 1. Dashboard waiting queue
await page.goto(BASE + '/');
await page.waitForSelector('#waiting-list .waiting-item');
let names = await page.locator('#waiting-list .waiting-item .activity-text').allTextContents();
check(JSON.stringify(names) === JSON.stringify(['Altdaten GmbH', 'Nordlicht Software GmbH', 'Müller Logistik AG', 'Same-Day AG']),
  `waiting (seit Bewerbung): ${names.join(', ')}`);
check((await page.textContent('#waiting-subtitle')).includes('Bewerbungsdatum'), 'subtitle names the measurement (Bewerbungsdatum)');
check((await page.locator('#waiting-list .waiting-item').first().textContent()).includes('50'), 'Altdaten: 50 Tage seit Bewerbung');
await page.click('[data-waiting-sort="SinceStatusChange"]');
await page.waitForFunction(() => document.querySelector('[data-waiting-sort="SinceStatusChange"]').getAttribute('aria-pressed') === 'true'
  && document.querySelector('#waiting-list .activity-text')?.textContent === 'Müller Logistik AG');
names = await page.locator('#waiting-list .waiting-item .activity-text').allTextContents();
check(JSON.stringify(names) === JSON.stringify(['Müller Logistik AG', 'Nordlicht Software GmbH', 'Same-Day AG', 'Altdaten GmbH']),
  `waiting (seit Statuswechsel): ${names.join(', ')}`);
check((await page.locator('#waiting-list .waiting-item').last().textContent()).includes('Kein Statuswechsel erfasst'), 'missing transition shown explicitly');
check(!(await page.textContent('#waiting-list')).match(/Rückmeldung|Reaktion/), 'no "employer response" wording');
check(!(await page.textContent('#waiting-list')).includes('Hanse') && !(await page.textContent('#waiting-list')).includes('Stadtwerke'), 'Absage and Entwurf excluded');
await page.screenshot({ path: path.join(SHOTS, 'm6-01-dashboard.png'), fullPage: true });

// 2. Verlauf: calendar time
await page.goto(BASE + '/verlauf.html');
await page.waitForSelector('.tl-row');
check(await page.locator('.tl-row').count() === 6, 'calendar mode: 6 rows');
const row = (app) => page.locator(`.tl-row[data-id="${app.id}"]`);
check(await row(A).locator('.tl-seg').count() === 4, 'Nordlicht: 4 status segments');
check(await row(A).locator('.marker-interview').count() === 1 && await row(A).locator('.marker-submitted').count() === 1, 'Nordlicht: interview + submission markers');
check(await row(B).locator('.marker-transition.is-backward').count() === 1, 'Müller: backward transition marked');
check(await row(E).locator('.tl-seg.is-uncertain').count() === 1, 'Altdaten: uncertain (striped) segment');
check(await row(F).locator('.marker-transition').count() === 2, 'Same-Day: both same-day transitions kept');
check(await page.locator('.tl-today').count() === 1, 'today line shown');
const left = async (locator) => (await locator.boundingBox()).x;
check(await left(row(C).locator('.tl-seg').nth(1)) < await left(row(B).locator('.tl-seg').first()),
  'shared calendar axis: Hanse rejection (−35 d) left of Müller start (−25 d)');
await page.screenshot({ path: path.join(SHOTS, 'm6-02-calendar.png') });

// 3. Selection, detail, keyboard
await row(A).locator('[data-select]').click();
check(await row(A).evaluate((el) => el.classList.contains('is-selected')), 'click selects row (highlighted)');
check(await page.getAttribute(`[data-select="${A.id}"]`, 'aria-pressed') === 'true', 'aria-pressed on selected row');
const detail = await page.textContent('#tl-detail');
check(detail.includes('Chronologie') && detail.includes('Zeitpunkt der Erfassung') && detail.includes('eingetragenes Bewerbungsdatum'),
  'detail distinguishes recorded transitions from entered submission date');
check(detail.includes('Termin') && detail.includes('aktuell'), 'detail: interview appointment + current open segment');
const selectedMarkers = await row(A).locator('.tl-marker[tabindex="0"]').count();
check(selectedMarkers > 0 && await page.locator('.tl-marker[tabindex="0"]').count() === selectedMarkers, 'only the selected row\'s markers are tabbable');
await page.keyboard.press('ArrowDown');
const focusedId = await page.evaluate(() => document.activeElement?.dataset.select);
check(focusedId && Number(focusedId) !== A.id && await page.locator(`.tl-row[data-id="${focusedId}"].is-selected`).count() === 1, 'ArrowDown moves focus and selection');
await page.keyboard.press('ArrowUp');
check(await page.evaluate(() => Number(document.activeElement?.dataset.select)) === A.id, 'ArrowUp returns');

// Tooltip on marker
await row(A).locator('.marker-transition').first().hover();
check((await page.textContent('#tl-tooltip')).includes('Status erfasst am') && await page.isVisible('#tl-tooltip'), 'tooltip on marker hover');
await page.mouse.move(5, 5);

// Open details from timeline
await page.click(`#tl-detail [data-open="${A.id}"]`);
await page.waitForSelector('dialog.side-panel[open]');
check(await page.getAttribute('#tab-btn-history', 'aria-selected') === 'true', '"Details öffnen" → detail panel on Verlauf tab');
await page.keyboard.press('Escape');
await page.waitForSelector('dialog.side-panel', { state: 'hidden' });

// 4. Days since submission
await page.click('[data-mode="day0"]');
await page.waitForFunction(() => document.querySelectorAll('.tl-row').length === 5);
check(true, 'Tage seit Bewerbung: application without submission date left out');
check((await page.textContent('#tl-notice')).includes('1 Bewerbung ohne'), 'notice explains the hidden application');
check(await page.locator('.tl-tick.is-strong:has-text("Tag 0")').count() === 1, 'Day 0 tick shown');
const zeroX = (await page.locator('.tl-gridlines > span.is-strong').boundingBox()).x;
check(await left(row(A).locator('.tl-seg').first()) < zeroX - 5, 'Nordlicht draft phase lies before Day 0');
check(Math.abs((await row(A).locator('.marker-submitted').boundingBox()).x + 10 - zeroX) < 3, 'submission markers aligned on Day 0');
check(Math.abs((await row(B).locator('.marker-submitted').boundingBox()).x - (await row(A).locator('.marker-submitted').boundingBox()).x) < 1, 'different submission dates share Day 0');
check(await page.isDisabled('#tl-range'), 'range select disabled in day-0 mode');
check((await page.textContent('#tl-detail')).includes('Tag 10'), 'detail shows "Tag N" labels');
await page.screenshot({ path: path.join(SHOTS, 'm6-03-day0.png') });
await page.click('[data-mode="calendar"]');
await page.waitForFunction(() => document.querySelectorAll('.tl-row').length === 6);

// 5. Range: last 30 days → last known state before the range
await page.selectOption('#tl-range', '30');
await page.waitForFunction(() => !document.querySelector('#tl-body .tl-state') && document.querySelectorAll('.tl-row').length > 0);
await row(C).locator('[data-select]').click();
check((await page.textContent('#tl-detail')).includes('Status zu Beginn des gewählten Zeitraums: Absage'), 'Hanse: state at range start = Absage');
await row(A).locator('[data-select]').click();
check((await page.textContent('#tl-detail')).includes('Status zu Beginn des gewählten Zeitraums: Beworben'), 'Nordlicht: state at range start = Beworben');
await page.selectOption('#tl-range', 'all');
await page.waitForFunction(() => document.querySelectorAll('.tl-row').length === 6);

// 6. Search, filter, sort, compare
await page.fill('#tl-search', 'müller');
check(await page.locator('.tl-row').count() === 1, 'search "müller" → 1 row');
await page.fill('#tl-search', 'gibt es nicht');
check(await page.isVisible('[data-reset]'), 'empty state with reset');
await page.click('#tl-body [data-reset]');
await page.selectOption('#tl-status', 'Absage');
check(await page.locator('.tl-row').count() === 1, 'status filter Absage → 1 row');
await page.selectOption('#tl-status', '');
await page.selectOption('#tl-sort', 'company');
check((await page.locator('.tl-company').first().textContent()) === 'Altdaten GmbH', 'sort A–Z');
await row(A).locator('[data-compare]').check();
await row(B).locator('[data-compare]').check();
check((await page.textContent('#tl-compare-bar')).includes('2 Bewerbungen'), 'compare bar counts marked rows');
await page.click('[data-compare-only]');
check(await page.locator('.tl-row').count() === 2, '"Nur markierte anzeigen" → 2 rows');
await page.click('[data-mode="day0"]');
check(await page.locator('.tl-row').count() === 2, 'comparison kept in day-0 mode');
await page.screenshot({ path: path.join(SHOTS, 'm6-04-compare.png') });
await page.click('[data-compare-clear]');
await page.click('[data-mode="calendar"]');
check(await page.locator('.tl-row').count() === 6, 'clearing comparison shows all');

// 7. Error state
await page.route('**/api/timeline**', (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{"title":"Testfehler"}' }));
await page.reload();
await page.waitForSelector('.tl-state.is-error');
check(await page.isVisible('[data-retry]'), 'error state with retry');
await page.unroute('**/api/timeline**');
await page.click('[data-retry]');
await page.waitForSelector('.tl-row');
check(true, 'retry loads the timeline');

// 8. Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.goto(BASE + '/verlauf.html');
await page.waitForSelector('.journey-card');
check(!(await page.isVisible('.tl-card')) && await page.locator('.journey-card').count() === 6, 'mobile: cards instead of timeline');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile: no horizontal overflow');
const card = page.locator(`.journey-card[data-id="${A.id}"]`);
check((await card.textContent()).includes('Beworben → In Prüfung'), 'mobile: status changes readable');
await page.screenshot({ path: path.join(SHOTS, 'm6-05-mobile.png'), fullPage: false });
await page.click('[data-mode="day0"]');
check((await card.textContent()).includes('Tag 0') && await page.locator('.journey-card').count() === 5, 'mobile: day-0 mode with "Tag" labels');
await card.locator('[data-open]').click();
await page.waitForSelector('dialog.side-panel[open]');
check(true, 'mobile: open details');
await page.keyboard.press('Escape');
await page.goto(BASE + '/');
await page.waitForSelector('#waiting-list .waiting-item');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile dashboard: no horizontal overflow');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
await browser.close();
