import { chromium } from 'playwright';
import { createSession } from './session.mjs';
import path from 'node:path';

const BASE = 'http://localhost:5299';
// M9: the suite works as its own registered user; fetch sends the login cookie and CSRF token.
const session = await createSession();
const fetch = session.fetch;
const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const SHOTS = path.join(DIR, 'shots');
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

// ---------- local date helpers ----------
const iso = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const plusDays = (n) => { const d = new Date(); d.setHours(0, 0, 0, 0); d.setDate(d.getDate() + n); return d; };
const today = plusDays(0);
const firstNextMonth = new Date(today.getFullYear(), today.getMonth() + 1, 1);
const monthName = (d) => new Intl.DateTimeFormat('de-DE', { month: 'long', year: 'numeric' }).format(d);

// ---------- API seed ----------
const json = (r) => r.json();
const send = (method, url, body) => fetch(BASE + url, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
const createApp = (body) => send('POST', '/api/applications', body).then(json);
const getApp = (id) => fetch(`${BASE}/api/applications/${id}`).then(json);

const nord = await createApp({ companyName: 'Nordlicht Software GmbH', position: 'Auszubildender FIAE', status: 'Beworben', appliedOn: iso(plusDays(-10)) });
const mueller = await createApp({ companyName: 'Müller Logistik AG', position: 'Fachinformatiker AE', status: 'Entwurf', deadline: iso(plusDays(3)) });
const kiel = await createApp({ companyName: 'Stadtwerke Kiel', position: 'Ausbildung Anwendungsentwicklung', status: 'Entwurf', deadline: iso(plusDays(-1)) });
const hanse = await createApp({ companyName: 'Hanse Data KG', position: 'Azubi Softwareentwicklung', status: 'Beworben', appliedOn: iso(plusDays(-2)), deadline: iso(plusDays(5)) });

let r = await send('POST', `/api/applications/${nord.id}/interviews`, { scheduledAt: `${iso(plusDays(1))}T14:30:00`, type: 'Video', location: 'https://meet.example.org/nordlicht', contactPerson: 'Frau Berg', status: 'Geplant' });
const interview1 = await r.json();
check(r.status === 201, `API: create interview → ${r.status}`);
r = await send('POST', `/api/applications/${nord.id}/interviews`, { scheduledAt: `${iso(firstNextMonth)}T10:00:00`, type: 'VorOrt', location: 'Hafenstraße 12, Hamburg', status: 'Geplant' });
const interview2 = await r.json();

r = await send('POST', '/api/applications/9999/interviews', { scheduledAt: `${iso(plusDays(1))}T10:00:00`, type: 'Video', status: 'Geplant' });
check(r.status === 404, `API: interview for missing application → ${r.status}`);
r = await send('POST', `/api/applications/${nord.id}/interviews`, { scheduledAt: null, type: 'Video', status: 'Geplant' });
check(r.status === 400 && (await r.json()).errors.scheduledAt, 'API: missing date → 400');
r = await send('PUT', '/api/interviews/9999', { scheduledAt: `${iso(plusDays(1))}T10:00:00`, type: 'Video', status: 'Geplant' });
check(r.status === 404, `API: update missing interview → ${r.status}`);
r = await fetch(`${BASE}/api/calendar?from=${iso(plusDays(10))}&to=${iso(plusDays(0))}`);
check(r.status === 400, `API: calendar with reversed range → ${r.status}`);
check((await getApp(nord.id)).application.status === 'Beworben', 'interview does not change application status');

// ---------- Browser ----------
const browser = await chromium.launch({ channel: 'msedge' });
const page = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 } })).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|404)/.test(m.text())) errors.push(m.text()); });

// 1. Dashboard: Anstehende Termine
await page.goto(BASE + '/');
await page.waitForSelector('#upcoming-events .upcoming-item');
const upcoming = await page.locator('#upcoming-events .upcoming-item').allTextContents();
check(upcoming.length === 4, `dashboard: 4 upcoming events (got ${upcoming.length}; submitted Hanse deadline excluded)`);
check(upcoming[0].includes('Stadtwerke Kiel') && upcoming[0].includes('Überfällig'), 'dashboard: overdue Kiel deadline first');
check(upcoming[1].includes('Nordlicht') && upcoming[1].includes('14:30'), 'dashboard: tomorrow\'s interview second');
check(upcoming[2].includes('Müller') && upcoming[2].includes('Läuft bald ab'), 'dashboard: Müller deadline third (läuft bald ab)');
check(upcoming[3].includes('Vor Ort'), 'dashboard: next month\'s interview last');
check(await page.isVisible('#activity-chart') && await page.isVisible('#status-chart'), 'dashboard charts still present');
await page.screenshot({ path: path.join(SHOTS, 'm5-01-dashboard.png'), fullPage: true });

// 2. Calendar month view
await page.goto(BASE + '/kalender.html');
await page.waitForSelector('.cal-day');
check(await page.textContent('#month-label') === monthName(today), `calendar shows ${monthName(today)}`);
check(await page.locator('.cal-day.is-today').count() === 1, 'today is highlighted');
const chipTomorrow = page.locator(`[data-date="${iso(plusDays(1))}"] .cal-chip`);
check((await chipTomorrow.textContent()).includes('14:30') && (await chipTomorrow.textContent()).includes('Nordlicht'), 'interview chip on tomorrow');
check(await page.locator(`[data-date="${iso(plusDays(-1))}"] .cal-chip.kind-Deadline.state-Overdue`).count() === 1, 'overdue deadline chip on yesterday');
check(await page.locator(`[data-date="${iso(plusDays(5))}"] .cal-chip.kind-Deadline.state-Done`).count() === 1, 'submitted application\'s deadline shown as done');
await page.screenshot({ path: path.join(SHOTS, 'm5-02-calendar.png') });

// Click a day → day panel
await page.click(`[data-date="${iso(plusDays(3))}"] .cal-date`);
check((await page.textContent('#day-panel')).includes('Müller Logistik AG'), 'day panel lists the selected day\'s events');
check(await page.getAttribute(`[data-date="${iso(plusDays(3))}"] .cal-date`, 'aria-pressed') === 'true', 'selected day marked (aria-pressed)');

// Click an event → detail panel on Termine tab
await chipTomorrow.click();
await page.waitForSelector('dialog.side-panel[open]');
check(await page.getAttribute('#tab-btn-schedule', 'aria-selected') === 'true', 'event opens detail panel on "Termine" tab');
check(await page.locator('.interview-row').count() === 2, 'Termine tab lists both interviews');
await page.screenshot({ path: path.join(SHOTS, 'm5-03-termine-tab.png') });
await page.keyboard.press('Escape');
await page.waitForSelector('dialog.side-panel', { state: 'hidden' });

// Month navigation across the boundary
await page.click('#next-month');
await page.waitForFunction((label) => document.getElementById('month-label').textContent === label, monthName(firstNextMonth));
check(await page.locator(`[data-date="${iso(firstNextMonth)}"]:not(.is-outside) .cal-chip.kind-Interview`).count() === 1, 'next month: interview on the 1st');
await page.click('#today');
await page.waitForFunction((label) => document.getElementById('month-label').textContent === label, monthName(today));
check(true, '"Heute" returns to the current month');
const cells = await page.locator('.cal-day').evaluateAll((els) => els.map((el) => el.dataset.date));
const firstCell = new Date(`${cells[0]}T00:00:00`);
check(cells.length % 7 === 0 && firstCell.getDay() === 1 && cells.includes(iso(new Date(today.getFullYear(), today.getMonth(), 1))),
  `grid: ${cells.length / 7} full weeks starting Monday ${cells[0]}`);

// 3. Plan interview from calendar
const planDay = iso(plusDays(2));
await page.click(`[data-date="${planDay}"] .cal-date`);
await page.click('[data-plan-day]');
await page.waitForSelector('#interview-form-title');
check(await page.inputValue('#i-scheduledAt') === `${planDay}T10:00`, 'form prefilled with selected day, 10:00');
await page.click('dialog[open] [data-submit]');
check((await page.textContent('#i-applicationId-error')).includes('Bewerbung'), 'missing application → German error');
await page.selectOption('#i-applicationId', String(mueller.id));
await page.fill('#i-scheduledAt', `${iso(plusDays(-1))}T10:00`);
await page.click('dialog[open] [data-submit]');
await page.waitForSelector('#i-scheduledAt-error:has-text("Vergangenheit")');
check(true, 'planned interview in the past → German error');
await page.fill('#i-scheduledAt', `${planDay}T09:15`);
await page.selectOption('#i-type', 'Telefon');
await page.fill('#i-contactPerson', 'Herr Albers');
await page.click('dialog[open] [data-submit]');
await page.waitForSelector(`[data-date="${planDay}"] .cal-chip:has-text("09:15")`);
check(true, 'new interview appears in the calendar');
let mApp = await getApp(mueller.id);
check(mApp.interviews.length === 1 && mApp.application.status === 'Entwurf', 'saved for Müller; status still Entwurf');
check(mApp.events.some((e) => e.description.includes('geplant')), 'history: "… geplant"');

// 4. Edit / cancel / delete in the detail panel
await page.goto(`${BASE}/bewerbungen.html?id=${nord.id}&tab=schedule`);
await page.waitForSelector('dialog.side-panel[open] .interview-row');
await page.locator(`[data-interview-id="${interview1.id}"] [data-edit-interview]`).click();
await page.waitForSelector('#interview-form-title:has-text("Gespräch bearbeiten")');
check(await page.inputValue('#i-scheduledAt') === `${iso(plusDays(1))}T14:30`, 'edit form prefilled with local time');
await page.fill('#i-scheduledAt', `${iso(plusDays(1))}T16:00`);
await page.fill('#i-notes', 'Projektbeispiele mitbringen');
await page.click('dialog[open] [data-submit]');
await page.waitForSelector(`[data-interview-id="${interview1.id}"] .interview-title:has-text("16:00")`);
check(await page.isVisible('#tab-schedule'), 'stays on Termine tab after editing');

await page.locator(`[data-interview-id="${interview2.id}"] [data-cancel]`).click();
await page.waitForSelector(`[data-interview-id="${interview2.id}"] .state-pill:has-text("Abgesagt")`);
check(true, 'cancel → status "Abgesagt"');

await page.locator(`[data-interview-id="${interview2.id}"] [data-delete-interview]`).click();
await page.waitForSelector('#confirm-title:has-text("Gespräch löschen?")');
await page.click('button[value="confirm"]');
await page.waitForFunction(() => document.querySelectorAll('.interview-row').length === 1);
check(true, 'delete with confirmation removes the interview');

await page.click('#tab-btn-history');
const history = await page.locator('.timeline-text').allTextContents();
check(history.some((t) => t.startsWith('Vorstellungsgespräch verschoben') && t.includes('14:30 Uhr → ') && t.includes('16:00 Uhr')), 'history: "verschoben 14:30 → 16:00"');
check(history.some((t) => t.startsWith('Vorstellungsgespräch abgesagt')), 'history: "abgesagt"');
check(history.some((t) => t.startsWith('Vorstellungsgespräch gelöscht')), 'history: "gelöscht"');
check(history.filter((t) => t.startsWith('Vorstellungsgespräch verschoben')).length === 1, 'notes change did not add another entry');
await page.keyboard.press('Escape');

// 5. Deadline: remove via "Frist ändern"
await page.goto(`${BASE}/bewerbungen.html?id=${mueller.id}&tab=schedule`);
await page.waitForSelector('dialog.side-panel[open] .deadline-box');
check((await page.textContent('.deadline-box')).includes('Läuft bald ab'), 'Termine tab: deadline "Läuft bald ab"');
await page.click('[data-edit-deadline]');
await page.waitForSelector('#application-form-title:has-text("Bewerbung bearbeiten")');
await page.fill('#f-deadline', '');
await page.click('dialog.dialog[open] [data-submit]');
await page.waitForSelector('#tab-schedule:has-text("Keine Bewerbungsfrist hinterlegt")');
mApp = await getApp(mueller.id);
check(mApp.application.deadline === null && mApp.events.some((e) => e.description.startsWith('Bewerbungsfrist entfernt')), 'deadline removed + history entry');
await page.keyboard.press('Escape');

// 6. Overdue shown in list and pipeline
await page.goto(BASE + '/bewerbungen.html');
await page.waitForSelector('tr[data-id]');
check((await page.locator(`tr[data-id="${kiel.id}"]`).textContent()).includes('überfällig'), 'list: Kiel deadline marked "überfällig"');
await page.goto(BASE + '/pipeline.html');
await page.waitForSelector('.kanban-card');
check((await page.locator(`.kanban-card[data-id="${kiel.id}"] .card-deadline`).textContent()).includes('überfällig'), 'pipeline card: overdue deadline');
check(await page.locator(`.kanban-card[data-id="${hanse.id}"] .card-deadline`).count() === 0, 'pipeline card: no deadline once submitted');

// 7. Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.goto(BASE + '/kalender.html');
await page.waitForSelector('.agenda-day');
check(!(await page.isVisible('.calendar-grid')) && await page.isVisible('#agenda'), 'mobile: agenda instead of month grid');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile: no horizontal overflow');
await page.screenshot({ path: path.join(SHOTS, 'm5-04-mobile-agenda.png'), fullPage: true });
await page.click('#next-month');
await page.waitForFunction((label) => document.getElementById('month-label').textContent === label, monthName(firstNextMonth));
await page.click('#today');
await page.waitForSelector('.agenda-item');
await page.locator('.agenda-item').first().click();
await page.waitForSelector('dialog.side-panel[open]');
check(await page.getAttribute('#tab-btn-schedule', 'aria-selected') === 'true', 'mobile: agenda item opens Termine tab');
await page.screenshot({ path: path.join(SHOTS, 'm5-05-mobile-panel.png') });
await page.keyboard.press('Escape');
await page.goto(BASE + '/');
await page.waitForSelector('#upcoming-events li');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile dashboard: no horizontal overflow');

// 8. Deleting an application removes its interviews from the calendar
await send('DELETE', `/api/applications/${nord.id}`);
const cal = await fetch(`${BASE}/api/calendar?from=${iso(plusDays(-3))}&to=${iso(plusDays(40))}`).then(json);
check(!cal.some((e) => e.applicationId === nord.id), 'deleted application: interviews gone from calendar');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
await browser.close();
