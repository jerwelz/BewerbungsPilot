import { chromium } from 'playwright';
import { createSession } from './session.mjs';

const BASE = 'http://localhost:5299';
// M9: the suite works as its own registered user; fetch sends the login cookie and CSRF token.
const session = await createSession();
const fetch = session.fetch;
const SHOTS = new URL('./shots/', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
const log = (ok, msg) => console.log(`${ok ? 'PASS' : 'FAIL'}  ${msg}`);
const check = (cond, msg) => { log(!!cond, msg); if (!cond) process.exitCode = 1; };

const browser = await chromium.launch({ channel: 'msedge' });
const page = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 } })).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
// 400 (validation test) and 404 (nonexistent id test) are expected responses, not script errors.
page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|404)/.test(m.text())) errors.push(m.text()); });
page.on('response', (r) => { if (r.status() === 404 && !r.url().includes('/api/applications/9999')) errors.push('404: ' + r.url()); });

// 1. Empty dashboard
await page.goto(BASE + '/');
await page.waitForSelector('.kpi');
check((await page.locator('.kpi-value').first().textContent()) === '0', 'empty dashboard shows 0 total');
check(await page.locator('#activity-box .chart-empty').isVisible(), 'empty activity chart shows message');
await page.screenshot({ path: SHOTS + '01-dashboard-empty.png' });

// 2. Create applications
await page.goto(BASE + '/bewerbungen.html');
async function create({ company, position, location = '', status = 'Beworben', appliedOn = '' }) {
  await page.click('#new-application');
  await page.fill('#f-companyName', company);
  await page.fill('#f-position', position);
  await page.fill('#f-location', location);
  await page.selectOption('#f-status', status);
  if (appliedOn) await page.fill('#f-appliedOn', appliedOn);
  await page.click('[data-submit]');
}
await create({ company: 'Nordlicht Software GmbH', position: 'Auszubildender FIAE', location: 'Hamburg', appliedOn: '2026-10-01' });
await page.waitForSelector('tr[data-id]');
await page.waitForSelector('dialog.dialog', { state: 'hidden' });
await create({ company: 'Müller Logistik AG', position: 'Fachinformatiker AE', location: 'Bremen', status: 'InPruefung', appliedOn: '2026-08-12' });
await page.waitForFunction(() => document.querySelectorAll('tr[data-id]').length === 2);
await page.waitForSelector('dialog.dialog', { state: 'hidden' });
await create({ company: 'Stadtwerke Kiel', position: 'Ausbildung Anwendungsentwicklung', location: 'Kiel', status: 'Entwurf' });
await page.waitForFunction(() => document.querySelectorAll('tr[data-id]').length === 3);
await page.waitForSelector('dialog.dialog', { state: 'hidden' });
check(true, 'created 3 applications via form');

// 3. Validation messages
await page.click('#new-application');
await page.click('[data-submit]');
await page.waitForSelector('[data-field="companyName"].has-error');
check((await page.textContent('#f-companyName-error')).includes('Bitte geben Sie das Unternehmen an'), 'German required-field error shown');
await page.keyboard.press('Escape');

// 4. Duplicate warning
await page.click('#new-application');
await page.fill('#f-companyName', '  nordlicht software gmbh ');
await page.fill('#f-position', 'AUSZUBILDENDER FIAE');
await page.click('[data-submit]');
await page.waitForSelector('[data-duplicate-warning]:not([hidden])');
check((await page.textContent('[data-submit]')).includes('Trotzdem speichern'), 'duplicate warning shown, button says "Trotzdem speichern"');
await page.screenshot({ path: SHOTS + '02-duplicate-warning.png' });
await page.click('[data-submit]');
await page.waitForFunction(() => document.querySelectorAll('tr[data-id]').length === 4);
check(true, 'user can save despite duplicate warning');
await page.waitForSelector('dialog.dialog', { state: 'hidden' });

// 5. Detail panel open, tabs, Escape
await page.locator('tr[data-id]', { hasText: 'Müller Logistik AG' }).click();
await page.waitForSelector('dialog.side-panel[open]');
await page.waitForTimeout(400);
check((await page.textContent('#panel-title')) === 'Müller Logistik AG', 'detail panel shows company');
await page.screenshot({ path: SHOTS + '03-detail-panel.png' });
await page.focus('#tab-btn-overview');
await page.keyboard.press('ArrowRight');
check(await page.locator('#tab-schedule').isVisible(), 'arrow key switches to Termine tab');
await page.keyboard.press('ArrowRight');
check(await page.locator('#tab-documents').isVisible(), 'arrow key switches to Dokumente tab');
await page.keyboard.press('ArrowRight');
check(await page.locator('#tab-history').isVisible(), 'arrow key switches to Verlauf tab');

// 6. Edit: change status → history entry
await page.click('[data-edit]');
await page.waitForSelector('#application-form-title:has-text("Bewerbung bearbeiten")');
check((await page.inputValue('#f-companyName')) === 'Müller Logistik AG', 'edit form prefilled');
await page.selectOption('#f-status', 'Vorstellungsgespraech');
await page.fill('#f-notes', 'Gespräch mit Frau Berg vereinbart.');
await page.click('[data-submit]');
await page.waitForSelector('.timeline-text:has-text("Status geändert: In Prüfung → Vorstellungsgespräch")');
check(true, 'status change appears in Verlauf without reload');
await page.screenshot({ path: SHOTS + '04-history.png' });

// Edit again without status change → no new entry
const before = await page.locator('.timeline-item').count();
await page.click('[data-edit]');
await page.fill('#f-location', 'Bremen (hybrid)');
await page.click('[data-submit]');
await page.waitForSelector('dialog.dialog', { state: 'hidden' });
await page.waitForTimeout(300);
check((await page.locator('.timeline-item').count()) === before, 'unchanged status creates no history entry');

await page.keyboard.press('Escape');
await page.waitForSelector('dialog.side-panel', { state: 'hidden' });
check(true, 'Escape closes detail panel');
check(await page.locator('tr', { hasText: 'Bremen (hybrid)' }).isVisible(), 'list updated after edit');

// 7. Search & filters
await page.fill('#filter-search', 'kiel');
check((await page.locator('tr[data-id]').count()) === 1, 'search "kiel" → 1 result');
check(await page.locator('#filter-reset').isVisible(), 'reset button visible while filtering');
await page.fill('#filter-search', 'gibtsnicht');
check(await page.locator('text=Keine passenden Bewerbungen').isVisible(), 'empty filter state shown');
await page.click('[data-empty-reset]');
check((await page.locator('tr[data-id]').count()) === 4, 'reset restores all rows');
await page.selectOption('#filter-status', 'Entwurf');
check((await page.locator('tr[data-id]').count()) === 2, 'status filter Entwurf → 2 (Kiel + duplicate saved as Entwurf)');
await page.selectOption('#filter-status', '');
await page.selectOption('#filter-period', 'none');
check((await page.locator('tr[data-id]').count()) === 2, 'period "noch nicht beworben" → 2');
await page.selectOption('#filter-period', '');
await page.selectOption('#filter-sort', 'company');
const firstCompany = await page.locator('tr[data-id] .row-link').first().textContent();
check(firstCompany === 'Müller Logistik AG', `sort A–Z first = ${firstCompany}`);
await page.screenshot({ path: SHOTS + '05-list.png' });
await page.click('#filter-reset');

// 8. Delete with confirmation (cancel first, then confirm)
await page.locator('tr[data-id]', { hasText: 'Stadtwerke Kiel' }).click();
await page.waitForSelector('dialog.side-panel[open]');
await page.click('[data-delete]');
await page.waitForSelector('#confirm-title');
await page.waitForTimeout(250);
await page.screenshot({ path: SHOTS + '06-delete-confirm.png' });
check((await page.textContent('#confirm-message')).includes('dauerhaft'), 'confirmation explains permanence');
await page.click('button[value="cancel"]');
await page.waitForTimeout(300);
check((await page.locator('tr[data-id]').count()) === 4, 'cancel keeps the application');
await page.click('[data-delete]');
await page.click('button[value="confirm"]');
await page.waitForFunction(() => document.querySelectorAll('tr[data-id]').length === 3);
check(!(await page.locator('dialog.side-panel[open]').count()), 'panel closed after delete');

// 9. Nonexistent id via URL
await page.goto(BASE + '/bewerbungen.html?id=9999');
await page.waitForSelector('.toast');
check((await page.textContent('.toast')).includes('nicht gefunden'), 'nonexistent id → German toast');

// 10. Dashboard with data
await page.goto(BASE + '/');
await page.waitForSelector('#activity-chart:not([hidden])');
await page.waitForTimeout(900);
const kpis = await page.locator('.kpi-value').allTextContents();
check(kpis.join(',') === '3,1,1,0,0', `KPIs = ${kpis.join(',')} (expected 3,1,1,0,0)`);
check((await page.locator('.activity-item').count()) > 0, 'recent activity listed');
await page.screenshot({ path: SHOTS + '07-dashboard.png', fullPage: true });
await page.locator('.activity-item').first().click();
await page.waitForSelector('dialog.side-panel[open]');
check(true, 'activity link opens detail panel');

// 11. Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.goto(BASE + '/');
await page.waitForTimeout(900);
const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
check(!overflow, 'dashboard: no horizontal page scroll at 375px');
await page.screenshot({ path: SHOTS + '08-mobile-dashboard.png', fullPage: true });
await page.goto(BASE + '/bewerbungen.html');
await page.waitForSelector('tr[data-id]');
const overflow2 = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
check(!overflow2, 'list: no horizontal page scroll at 375px');
await page.screenshot({ path: SHOTS + '09-mobile-list.png' });
await page.locator('tr[data-id]').first().click();
await page.waitForTimeout(400);
await page.screenshot({ path: SHOTS + '10-mobile-panel.png' });

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
await browser.close();
