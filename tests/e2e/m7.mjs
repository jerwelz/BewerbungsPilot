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

const iso = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const day = (offset, hour = 10) => { const d = new Date(); d.setHours(hour, 0, 0, 0); d.setDate(d.getDate() + offset); return d; };
const dbTime = (d) => d.toISOString().replace('T', ' ').slice(0, 19);

const json = (r) => r.json();
const send = (method, url, body) => fetch(BASE + url, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
const create = (company, status, appliedOffset) => send('POST', '/api/applications', {
  companyName: company, position: 'Auszubildender FIAE', status, appliedOn: appliedOffset === null ? null : iso(day(appliedOffset)),
}).then(json);
const interview = (app, offset, status) => send('POST', `/api/applications/${app.id}/interviews`,
  { scheduledAt: `${iso(day(offset))}T10:00:00`, type: 'Video', status }).then(json);

// ---------- seed ----------
const A = await create('Nordlicht Software GmbH', 'Absage', -40);       // 2 interviews, then rejection
const B = await create('Müller Logistik AG', 'Angebot', -30);           // offer without interview
const C = await create('Hanse Data KG', 'Vorstellungsgespraech', -100); // outside 90 days
const D = await create('Stadtwerke Kiel', 'Entwurf', null);             // draft, no date
const E = await create('Altdaten GmbH', 'InPruefung', -50);             // no history at all
const F = await create('Same-Day AG', 'Beworben', -8);                  // planned interview (recorded today)
const G = await create('Abgesagt GmbH', 'Beworben', -15);               // only a cancelled interview
const H = await create('Ohne Datum AG', 'InPruefung', null);            // submitted per status, date removed below
const iA1 = await interview(A, -20, 'Abgeschlossen');
const iA2 = await interview(A, -16, 'Abgeschlossen');
await interview(F, 3, 'Geplant');
await interview(G, 5, 'Abgesagt');

const db = new DatabaseSync(path.join(process.env.DB_DIR, 'bewerbungspilot.db'));
function history(app, entries, { noHistory = false } = {}) {
  db.prepare(`DELETE FROM ApplicationEvents WHERE JobApplicationId = ? AND Type IN ('Created','StatusChanged')`).run(app.id);
  const insert = db.prepare('INSERT INTO ApplicationEvents (JobApplicationId, Type, FromStatus, ToStatus, Description, OccurredAt) VALUES (?,?,?,?,?,?)');
  if (!noHistory) entries.forEach(([status, offset], i) =>
    insert.run(app.id, i === 0 ? 'Created' : 'StatusChanged', i === 0 ? null : entries[i - 1][0], status, 'seed', dbTime(day(offset))));
  db.prepare('UPDATE Applications SET CreatedAt = ?, StatusChangedAt = ? WHERE Id = ?')
    .run(dbTime(day(entries[0][1])), dbTime(day(entries.at(-1)[1])), app.id);
}
history(A, [['Beworben', -40], ['Vorstellungsgespraech', -20], ['Absage', -5]]);
history(B, [['Beworben', -30], ['Angebot', -10]]);
history(C, [['Beworben', -100], ['Vorstellungsgespraech', -60]]);
history(D, [['Entwurf', -3]]);
history(E, [['InPruefung', -50]], { noHistory: true });
history(F, [['Beworben', -8]]);
history(G, [['Beworben', -15]]);
history(H, [['InPruefung', -12]]);
db.prepare('UPDATE Applications SET AppliedOn = NULL WHERE Id = ?').run(H.id);
db.prepare('UPDATE Interviews SET CreatedAt = ? WHERE Id = ?').run(dbTime(day(-22)), iA1.id);
db.prepare('UPDATE Interviews SET CreatedAt = ? WHERE Id = ?').run(dbTime(day(-18)), iA2.id);
db.close();

// ---------- API ----------
const analysis = (q = '') => fetch(`${BASE}/api/analysis${q}`).then(json);
const all = await analysis();
const count = (r, m) => r.milestones.find((x) => x.milestone === m).count;
check(all.cohortSize === 6, `all time: cohort 6 (got ${all.cohortSize})`);
check(count(all, 'Submitted') === 6 && count(all, 'Interview') === 3 && count(all, 'Offer') === 1,
  `all time: submitted 6, interview 3, offer 1 (got ${count(all, 'Submitted')}/${count(all, 'Interview')}/${count(all, 'Offer')})`);
check(JSON.stringify(all.outcomes) === JSON.stringify({ open: 4, offer: 1, rejected: 1, unknown: 0 }), `outcomes ${JSON.stringify(all.outcomes)}`);
check(all.incompleteHistoryCount === 1, 'one incomplete history (Altdaten)');
check(all.withoutSubmissionDate.drafts === 1 && all.withoutSubmissionDate.other === 1, 'without date: 1 draft + 1 other');
check(all.milestones.every((m) => m.percent === null), 'no percentages below 10 applications');
check(!JSON.stringify(all).match(/"notes"|"location"|"jobUrl"/), 'no private fields in response');
const interviewIds = all.milestones.find((m) => m.milestone === 'Interview').applicationIds;
check(!interviewIds.includes(B.id) && !interviewIds.includes(G.id), 'offer-only and cancelled-only applications not counted as interview');

const last90 = await analysis(`?from=${iso(day(-90))}&to=${iso(day(0))}`);
check(last90.cohortSize === 5 && count(last90, 'Interview') === 2, 'last 90 days: Hanse excluded (cohort 5, interview 2)');
const cut = await analysis(`?cutoff=${iso(day(-19))}`);
check(cut.cohortSize === 4 && count(cut, 'Interview') === 2 && count(cut, 'Offer') === 0, `cutoff −19 d: cohort 4, interview 2, offer 0 (got ${cut.cohortSize}/${count(cut, 'Interview')}/${count(cut, 'Offer')})`);
check(cut.applications.find((a) => a.id === A.id).statusAtCutoff === 'Vorstellungsgespraech', 'status at cutoff reconstructed from history');
let r = await fetch(`${BASE}/api/analysis?cutoff=${iso(day(5))}`);
check(r.status === 400, `future cutoff → ${r.status}`);
r = await fetch(`${BASE}/api/analysis?from=${iso(day(0))}&to=${iso(day(-5))}`);
check(r.status === 400, `reversed period → ${r.status}`);

// ---------- Browser ----------
const browser = await chromium.launch({ channel: 'msedge' });
const page = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 } })).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|500)/.test(m.text())) errors.push(m.text()); });

await page.goto(BASE + '/analyse.html');
await page.waitForSelector('.milestone');
check((await page.textContent('#an-context')).includes('6 Bewerbungen'), 'context names cohort size');
check((await page.textContent('#an-context')).includes('Auswertungsstand'), 'context names observation cutoff');
const uiCount = async (m) => Number(await page.textContent(`[data-milestone="${m}"] .milestone-count strong`));
check(await uiCount('Interview') === 3 && (await page.textContent('[data-milestone="Interview"] .milestone-count')).includes('von 6'), 'interview bar: "3 von 6"');
check((await page.textContent('[data-milestone="Interview"]')).includes('3 ohne dokumentierten Nachweis'), 'remainder labeled "ohne dokumentierten Nachweis"');
check((await page.textContent('#an-notes')).includes('keine Prozentwerte'), 'small-sample note shown');
check((await page.textContent('#an-notes')).includes('unvollständig'), 'incomplete-history note shown');
await page.screenshot({ path: path.join(SHOTS, 'm7-01-overview.png'), fullPage: true });

// Drill-down consistency for every milestone
for (const m of ['Submitted', 'Interview', 'Offer']) {
  await page.click(`[data-milestone="${m}"]`);
  await page.waitForSelector('#an-drill:not([hidden]) .drill-header');
  const items = await page.locator('#an-drill .drill-item').count();
  const shown = await uiCount(m);
  const expectedIds = all.milestones.find((x) => x.milestone === m).applicationIds.length;
  check(items === shown && shown === expectedIds, `${m}: count ${shown} = drill-down ${items} = API ${expectedIds}`);
  await page.click('[data-back]');
  await page.waitForSelector('#an-drill', { state: 'hidden' });
}

await page.click('[data-milestone="Interview"]');
await page.waitForSelector('#an-drill .drill-item');
const drillText = await page.textContent('#an-drill');
check(drillText.includes('Nordlicht') && drillText.includes('Hanse') && drillText.includes('Same-Day'), 'interview drill-down: Nordlicht, Hanse, Same-Day');
check(!drillText.includes('Müller') && !drillText.includes('Abgesagt GmbH'), 'interview drill-down excludes offer-only and cancelled-only');
const nordItem = page.locator('.drill-item', { hasText: 'Nordlicht' });
check((await nordItem.locator('.drill-evidence li').count()) === 3, 'Nordlicht: 3 pieces of evidence (status + 2 interviews), counted once');
check((await nordItem.textContent()).includes('erfasst am') && (await nordItem.textContent()).includes('eingetragen am'), 'evidence shows recording dates');
await page.screenshot({ path: path.join(SHOTS, 'm7-02-drill.png'), fullPage: true });
await nordItem.locator('[data-open]').click();
await page.waitForSelector('dialog.side-panel[open]');
check(true, 'drill-down opens application detail');
await page.keyboard.press('Escape');
await page.waitForSelector('dialog.side-panel', { state: 'hidden' });

// Keyboard: Enter opens drill-down, focus moves; Escape returns focus
await page.click('[data-back]');
await page.focus('[data-milestone="Offer"]');
await page.keyboard.press('Enter');
await page.waitForSelector('#an-drill:not([hidden])');
check(await page.evaluate(() => document.activeElement?.id) === 'an-drill-title', 'Enter: focus moves to drill-down heading');
check((await page.textContent('#an-drill')).includes('Müller') && await page.locator('#an-drill .drill-item').count() === 1, 'offer drill-down: only Müller');
await page.keyboard.press('Escape');
await page.waitForSelector('#an-drill', { state: 'hidden' });
check(await page.evaluate(() => document.activeElement?.dataset.milestone) === 'Offer', 'Escape: back to overview, focus on milestone');

// Without submission date
await page.click('#an-notes [data-milestone="WithoutDate"]');
await page.waitForSelector('#an-drill .drill-item');
const wd = await page.textContent('#an-drill');
check(wd.includes('Stadtwerke Kiel') && wd.includes('Ohne Datum AG') && await page.locator('#an-drill .drill-item').count() === 2, 'applications without date listed explicitly');
await page.click('[data-back]');

// Period filter + cutoff keep counts and drill-down consistent
await page.selectOption('#an-period', '90');
await page.waitForFunction(() => document.getElementById('an-context').textContent.includes('5 Bewerbungen'));
check(await uiCount('Interview') === 2, 'last 90 days: interview 2');
await page.click('[data-milestone="Interview"]');
await page.waitForSelector('#an-drill .drill-item');
check(await page.locator('#an-drill .drill-item').count() === 2, 'last 90 days: drill-down 2');
await page.selectOption('#an-period', 'all');
await page.waitForFunction(() => document.getElementById('an-context').textContent.includes('6 Bewerbungen'));
check(await page.locator('#an-drill .drill-item').count() === 3, 'drill-down follows filter change (3)');
await page.fill('#an-cutoff', iso(day(-19)));
await page.dispatchEvent('#an-cutoff', 'change');
await page.waitForFunction(() => document.getElementById('an-context').textContent.includes('4 Bewerbungen'));
check(await uiCount('Interview') === 2 && await page.locator('#an-drill .drill-item').count() === 2, 'cutoff: count and drill-down both 2');
check((await page.textContent('#an-drill')).includes('Zum Auswertungsstand: Vorstellungsgespräch'), 'drill-down shows status at cutoff');
await page.fill('#an-cutoff', iso(day(0)));
await page.dispatchEvent('#an-cutoff', 'change');
await page.click('[data-back]');

// Custom period: small and empty
await page.selectOption('#an-period', 'custom');
await page.fill('#an-from', iso(day(-35)));
await page.fill('#an-to', iso(day(-25)));
await page.dispatchEvent('#an-to', 'change');
await page.waitForFunction(() => document.getElementById('an-context').textContent.includes('1 Bewerbung'));
check((await page.textContent('#an-context')).includes('vom'), 'custom period shown in context');
await page.fill('#an-from', '2020-01-01');
await page.fill('#an-to', '2020-12-31');
await page.dispatchEvent('#an-to', 'change');
await page.waitForFunction(() => document.getElementById('an-context').textContent.includes('0 Bewerbungen'));
check((await page.textContent('#an-milestones')).includes('keine Bewerbungen'), 'empty state for empty cohort');

// Error state
await page.route('**/api/analysis**', (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{"title":"Testfehler"}' }));
await page.selectOption('#an-period', 'all');
await page.waitForSelector('[data-retry]');
await page.unroute('**/api/analysis**');
await page.click('[data-retry]');
await page.waitForSelector('.milestone');
check(true, 'error state with working retry');

// Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.reload();
await page.waitForSelector('.milestone');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile: no horizontal overflow');
const periodHeight = (await page.locator('#an-period').boundingBox()).height;
check(periodHeight < 50, `mobile: period select has normal height (${Math.round(periodHeight)}px)`);
await page.click('[data-milestone="Interview"]');
await page.waitForSelector('#an-drill .drill-item');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile drill-down: no horizontal overflow');
await page.screenshot({ path: path.join(SHOTS, 'm7-03-mobile.png'), fullPage: true });

// Reduced motion
const rm = await (await session.newContext(browser, { viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' })).newPage();
await rm.goto(BASE + '/analyse.html');
await rm.waitForSelector('.milestone-fill');
check(parseFloat(await rm.evaluate(() => getComputedStyle(document.querySelector('.milestone-fill')).transitionDuration)) === 0, 'reduced motion: no bar transition');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
await browser.close();
