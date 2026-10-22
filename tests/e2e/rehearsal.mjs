// Read-only smoke test for the migration rehearsal on a COPY of real data (never on the original).
// Not part of run.mjs. Usage (app running on BASE against the copy):
//   REHEARSAL_EMAIL=… REHEARSAL_PASSWORD=… EXPECTED=<counts.json> node rehearsal.mjs
// EXPECTED: { counts: { Applications, ApplicationEvents, Documents, Interviews }, deadlines,
//             confirmed_locations, documents: [{ id, sha256 }] }
// Prints only counts and checks – no application content.
import { chromium } from 'playwright';
import crypto from 'node:crypto';
import fs from 'node:fs';
import { createSession, BASE } from './session.mjs';

const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };
const expected = JSON.parse(fs.readFileSync(process.env.EXPECTED, 'utf8'));
const owner = await createSession({ email: process.env.REHEARSAL_EMAIL, password: process.env.REHEARSAL_PASSWORD, register: false });
const get = (s, url) => s.fetch(BASE + url).then((r) => r.json());

// ---------- API: everything is there and belongs to the rehearsal account ----------
const applications = await get(owner, '/api/applications');
check(applications.length === expected.counts.Applications, `applications: ${applications.length} (expected ${expected.counts.Applications})`);
let events = 0, documents = [], interviews = 0;
for (const a of applications) {
  const detail = await get(owner, `/api/applications/${a.id}`);
  events += detail.events.length;
  documents.push(...detail.documents);
  interviews += detail.interviews.length;
}
check(events === expected.counts.ApplicationEvents, `history entries: ${events} (expected ${expected.counts.ApplicationEvents})`);
check(documents.length === expected.counts.Documents, `documents: ${documents.length} (expected ${expected.counts.Documents})`);
check(interviews === expected.counts.Interviews, `interviews: ${interviews} (expected ${expected.counts.Interviews})`);
check(applications.filter((a) => a.deadline).length === expected.deadlines, `deadlines: ${applications.filter((a) => a.deadline).length} (expected ${expected.deadlines})`);
const map = await owner.fetch(BASE + '/api/map/query', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ origin: null }) }).then((r) => r.json());
const located = map.items.filter((i) => i.latitude !== null || i.precision === 'Remote').length;
check(located === expected.confirmed_locations, `confirmed locations: ${located} (expected ${expected.confirmed_locations})`);
for (const doc of expected.documents) {
  const bytes = Buffer.from(await (await owner.fetch(`${BASE}/api/documents/${doc.id}/download`)).arrayBuffer());
  check(crypto.createHash('sha256').update(bytes).digest('hex') === doc.sha256, `document ${doc.id}: download SHA-256 matches the stored fingerprint`);
}

// ---------- Browser: every page works for the owner ----------
const browser = await chromium.launch({ channel: 'msedge' });
const context = await owner.newContext(browser, { viewport: { width: 1440, height: 900 } });
await context.route('https://tile.openstreetmap.org/**', (route) => route.fulfill({ status: 404 }));
const page = await context.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
for (const path of ['/', '/bewerbungen.html', '/pipeline.html', '/kalender.html', '/verlauf.html', '/analyse.html', '/standorte.html']) {
  await page.goto(BASE + path);
  await page.waitForLoadState('networkidle');
  check(page.url().startsWith(BASE + path), `owner can open ${path}`);
}
await page.goto(BASE + '/bewerbungen.html');
await page.waitForSelector('tr[data-id]');
check(await page.locator('tr[data-id]').count() === expected.counts.Applications, 'Bewerbungen table shows every application');
for (const doc of documents) {
  await page.goto(`${BASE}/bewerbungen.html?id=${doc.applicationId}`);
  await page.waitForSelector('dialog.side-panel[open]');
  await page.click('#tab-btn-documents');
  await page.locator('.document-row', { hasText: doc.originalFileName }).first().locator('[data-preview]').first().click();
  await page.waitForSelector('[data-preview-frame]:not([hidden])', { timeout: 8000 }).catch(() => {});
  check(await page.isVisible('[data-preview-frame]'), `document ${doc.id}: preview opens in the browser`);
}
check(errors.length === 0, `no browser errors${errors.length ? ': ' + errors.join(' | ') : ''}`);

// ---------- A second account sees none of it ----------
const other = await createSession();
const names = applications.map((a) => a.companyName);
check((await get(other, '/api/applications')).length === 0, 'second account: no applications via the API');
check((await other.fetch(`${BASE}/api/documents/${expected.documents[0]?.id ?? 1}/download`)).status === 404, 'second account: document download → 404');
const otherPage = await (await other.newContext(browser, { viewport: { width: 1440, height: 900 } })).newPage();
for (const path of ['/', '/bewerbungen.html', '/standorte.html', '/verlauf.html']) {
  await otherPage.goto(BASE + path);
  await otherPage.waitForLoadState('networkidle');
  const html = await otherPage.content();
  check(names.every((n) => !html.includes(n)), `second account on ${path}: none of the owner's companies visible`);
}
await browser.close();
