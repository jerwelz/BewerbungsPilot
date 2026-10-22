import { chromium } from 'playwright';
import { createSession } from './session.mjs';
import fs from 'node:fs';
import path from 'node:path';

const BASE = 'http://localhost:5299';
// M9: the suite works as its own registered user; fetch sends the login cookie and CSRF token.
const session = await createSession();
const fetch = session.fetch;
const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const SHOTS = path.join(DIR, 'shots');
const DOC_DIR = path.join(process.env.DB_DIR, 'documents');
// Playwright reports the built-in PDF viewer's internal frame as closed during teardown; that is not an app error.
process.on('unhandledRejection', (e) => { if (e?.name !== 'TargetClosedError') { console.error(e); process.exitCode = 1; } });
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

// --- test files ---
function makePdf(text) {
  const stream = `BT /F1 28 Tf 72 760 Td (${text}) Tj ET`;
  return Buffer.from(`%PDF-1.4
1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj
4 0 obj<</Length ${stream.length}>>stream
${stream}
endstream endobj
5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj
trailer<</Root 1 0 R>>
%%EOF
`, 'latin1');
}
const files = path.join(DIR, 'files');
fs.mkdirSync(files, { recursive: true });
const cvPath = path.join(files, 'Lebenslauf_Oktober.pdf');
fs.writeFileSync(cvPath, makePdf('Lebenslauf Oktober - Max Muster'));
const txtPath = path.join(files, 'notizen.txt');
fs.writeFileSync(txtPath, 'nur Text');
const fakePath = path.join(files, 'rechnung.pdf');
fs.writeFileSync(fakePath, Buffer.from('MZ\x90\x00 not a pdf at all', 'latin1'));

// --- API helpers ---
const json = (r) => r.json();
const post = (body) => fetch(BASE + '/api/applications', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).then(json);
function upload(appId, bytes, name, category, headers = {}) {
  const form = new FormData();
  form.append('file', new Blob([bytes], { type: 'application/pdf' }), name);
  if (category) form.append('category', category);
  return fetch(`${BASE}/api/applications/${appId}/documents`, { method: 'POST', body: form, headers });
}
const storedFiles = () => (fs.existsSync(DOC_DIR) ? fs.readdirSync(DOC_DIR) : []);

const appA = await post({ companyName: 'Nordlicht Software GmbH', position: 'Auszubildender FIAE', status: 'Beworben', appliedOn: '2026-10-01' });
const appB = await post({ companyName: 'Müller Logistik AG', position: 'Fachinformatiker AE', status: 'Beworben', appliedOn: '2026-10-05' });

// ===== Endpoint checks =====
let r = await upload(9999, makePdf('x'), 'a.pdf', 'Lebenslauf');
check(r.status === 404, `upload to missing application → ${r.status}`);
r = await upload(appA.id, makePdf('x'), 'a.pdf', '');
check(r.status === 400 && (await r.json()).errors.category, 'missing category → 400 with category error');
r = await upload(appA.id, Buffer.from('MZ fake'), 'a.pdf', 'Lebenslauf');
check(r.status === 400 && (await r.json()).errors.file[0].includes('keine gültige PDF'), 'renamed exe → 400 "keine gültige PDF-Datei"');
const big11 = Buffer.alloc(11 * 1024 * 1024); makePdf('x').copy(big11);
r = await upload(appA.id, big11, 'gross.pdf', 'Zeugnis');
check(r.status === 400 && (await r.json()).errors.file[0].includes('10 MB'), `11 MB file → ${r.status} German size error`);
const big20 = Buffer.alloc(20 * 1024 * 1024); makePdf('x').copy(big20);
// The server stops reading after ~12 MB; it either answers 413 or closes the connection mid-upload.
const big20result = await upload(appA.id, big20, 'riesig.pdf', 'Zeugnis').then((res) => res.status, (e) => e.cause?.code ?? 'error');
check(big20result === 413 || big20result === 'ECONNABORTED' || big20result === 'ECONNRESET', `20 MB file rejected by server (${big20result})`);
r = await upload(appA.id, makePdf('x'), 'a.pdf', 'Lebenslauf', { 'Sec-Fetch-Site': 'cross-site' });
check(r.status === 403, `cross-site upload → ${r.status}`);
check(storedFiles().length === 0, 'no files stored after rejected uploads');

// Independent versions with identical original names
const v1 = await (await upload(appA.id, makePdf('Version Oktober'), 'Lebenslauf.pdf', 'Lebenslauf')).json();
const v2 = await (await upload(appB.id, makePdf('Version November'), 'Lebenslauf.pdf', 'Lebenslauf')).json();
const d1 = Buffer.from(await (await fetch(`${BASE}/api/documents/${v1.id}/download`)).arrayBuffer());
const d2 = Buffer.from(await (await fetch(`${BASE}/api/documents/${v2.id}/download`)).arrayBuffer());
check(d1.equals(makePdf('Version Oktober')) && d2.equals(makePdf('Version November')), 'two "Lebenslauf.pdf" uploads stay independent (byte-exact)');
check(v1.storedFileName === undefined, 'internal file name not exposed in API');

r = await fetch(`${BASE}/api/documents/${v1.id}/download`);
check(/^attachment;/.test(r.headers.get('content-disposition')) && r.headers.get('content-disposition').includes('Lebenslauf.pdf'), `download header: ${r.headers.get('content-disposition')}`);
r = await fetch(`${BASE}/api/documents/${v1.id}/preview`);
check(/^inline;/.test(r.headers.get('content-disposition')) && r.headers.get('content-type') === 'application/pdf', 'preview: inline + application/pdf');
check(r.headers.get('x-content-type-options') === 'nosniff' && r.headers.get('cache-control') === 'no-store', 'nosniff + no-store headers');
r = await fetch(`${BASE}/api/documents/99999/download`);
check(r.status === 404, `invalid document id → ${r.status}`);
r = await fetch(`${BASE}/api/documents/99999`, { method: 'DELETE' });
check(r.status === 404, `delete invalid document id → ${r.status}`);

const stored = storedFiles();
check(stored.length === 2 && stored.every((f) => /^[0-9a-f]{32}\.pdf$/.test(f)), `files stored as GUID names in storage/documents: ${stored.join(', ')}`);
for (const url of [`/documents/${stored[0]}`, `/storage/documents/${stored[0]}`, `/${stored[0]}`]) {
  r = await fetch(BASE + url);
  check(r.status === 404, `no static access ${url} → ${r.status}`);
}
r = await fetch(`${BASE}/api/documents/..%2F..%2Fappsettings.json/download`);
check(r.status === 404, `path traversal attempt → ${r.status}`);

// ===== Browser workflow =====
const browser = await chromium.launch({ channel: 'msedge' });
const context = await session.newContext(browser, { viewport: { width: 1440, height: 900 }, acceptDownloads: true });
const page = await context.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|404)/.test(m.text())) errors.push(m.text()); });

await page.goto(`${BASE}/bewerbungen.html?id=${appA.id}`);
await page.waitForSelector('dialog.side-panel[open]');
await page.click('#tab-btn-documents');
check(await page.locator('.document-row').count() === 1, 'Dokumente tab lists existing document');

// Drag-over highlight + drop
const dz = page.locator('[data-dropzone]');
const dt = await page.evaluateHandle(() => {
  const transfer = new DataTransfer();
  transfer.items.add(new File(['%PDF-1.4\n%%EOF\n'], 'Anschreiben_Nordlicht.pdf', { type: 'application/pdf' }));
  return transfer;
});
await dz.dispatchEvent('dragenter', { dataTransfer: dt });
check(await dz.evaluate((el) => el.classList.contains('is-dragover')), 'dropzone highlighted while dragging a file over it');
await page.screenshot({ path: path.join(SHOTS, 'm4-01-dragover.png') });
await dz.dispatchEvent('drop', { dataTransfer: dt });
check(await page.locator('[data-staged]').isVisible(), 'dropped file is staged');
check(!(await page.locator('[data-dropzone]').isVisible()), 'dropzone hidden while a file is staged');
check(await page.textContent('[data-staged-name]') === 'Anschreiben_Nordlicht.pdf', 'staged file name shown');
await page.click('[data-upload-submit]');
check((await page.textContent('#doc-category-error')).includes('Kategorie'), 'missing category → German error');
await page.selectOption('#doc-category', 'Anschreiben');
await page.click('[data-upload-submit]');
await page.waitForFunction(() => document.querySelectorAll('.document-row').length === 2);
check(true, 'drop upload succeeded (2 documents)');

// File picker
await page.setInputFiles('[data-file-input]', txtPath);
check((await page.textContent('[data-file-error]')).includes('nur PDF'), '.txt via picker → "Es werden nur PDF-Dateien unterstützt."');
await page.setInputFiles('[data-file-input]', fakePath);
await page.selectOption('#doc-category', 'Sonstiges');
await page.click('[data-upload-submit]');
await page.waitForSelector('[data-file-error]:has-text("keine gültige PDF")');
check(true, 'fake PDF via picker → server error shown');
await page.click('[data-unstage]');
await page.setInputFiles('[data-file-input]', cvPath);
await page.selectOption('#doc-category', 'Lebenslauf');
await page.click('[data-upload-submit]');
await page.waitForSelector('.toast:has-text("Lebenslauf_Oktober.pdf")');
await page.waitForFunction(() => document.querySelectorAll('.document-row').length === 3);
check(await page.locator('#tab-documents').isVisible(), 'stays on Dokumente tab after upload');
check(!(await page.locator('[data-staged]').isVisible()) && await page.locator('[data-dropzone]').isVisible(), 'after upload: staging area hidden, dropzone back');
check((await page.textContent('#tab-btn-documents')).includes('3'), 'tab counter = 3');
await page.waitForTimeout(400);
await page.screenshot({ path: path.join(SHOTS, 'm4-02-documents.png') });

// Preview
const row = page.locator('.document-row', { hasText: 'Lebenslauf_Oktober.pdf' });
await row.locator('[data-preview]').first().click();
await page.waitForSelector('.preview-dialog[open]');
check(await page.textContent('#preview-title') === 'Lebenslauf_Oktober.pdf', 'preview shows file name');
check(await page.textContent('[data-preview-category]') === 'Lebenslauf', 'preview shows category');
await page.waitForSelector('[data-preview-frame]:not([hidden])', { timeout: 8000 }).catch(() => {});
const frameSrc = await page.getAttribute('[data-preview-frame]', 'src');
check(frameSrc?.includes('/preview'), `preview iframe points to preview endpoint (${frameSrc})`);
check(await page.isVisible('[data-preview-frame]'), 'preview iframe loaded (loading state cleared)');
await page.waitForTimeout(1200);
await page.screenshot({ path: path.join(SHOTS, 'm4-03-preview.png') });
await page.keyboard.press('Escape');
await page.waitForSelector('.preview-dialog', { state: 'hidden' });
check(await page.isVisible('dialog.side-panel[open]'), 'Escape closes preview, panel stays open');

// Download
const [download] = await Promise.all([page.waitForEvent('download'), row.locator('a[download]').click()]);
const downloadedPath = await download.path();
check(download.suggestedFilename() === 'Lebenslauf_Oktober.pdf', `download filename = ${download.suggestedFilename()}`);
check(fs.readFileSync(downloadedPath).equals(fs.readFileSync(cvPath)), 'downloaded bytes identical to upload');

// Delete with confirmation
const before = storedFiles().length;
await row.locator('[data-delete-document]').click();
await page.waitForSelector('#confirm-title:has-text("Dokument löschen?")');
await page.click('button[value="cancel"]');
await page.waitForTimeout(300);
check(await page.locator('.document-row').count() === 3, 'cancel keeps the document');
await row.locator('[data-delete-document]').click();
await page.click('button[value="confirm"]');
await page.waitForFunction(() => document.querySelectorAll('.document-row').length === 2);
check(storedFiles().length === before - 1, 'file removed from storage after delete');
await page.click('#tab-btn-history');
check(await page.locator('.timeline-text:has-text("Dokument gelöscht")').count() === 1, 'history shows "Dokument gelöscht"');
check(await page.locator('.timeline-text:has-text("Dokument hinzugefügt")').count() === 3, 'history shows 3 "Dokument hinzugefügt"');

// Preview error state for a vanished document
await page.click('#tab-btn-documents');
await page.route('**/api/documents/*/preview', (route) => route.fulfill({ status: 404 }));
await page.locator('.document-row [data-preview]').first().click();
await page.waitForSelector('[data-preview-status].is-error');
check(true, 'preview shows German error state when file is missing');
await page.click('[data-preview-close]');
await page.unroute('**/api/documents/*/preview');

// Deleting the application removes its files
await page.keyboard.press('Escape');
const filesBefore = storedFiles().length;
const docCountA = (await (await fetch(`${BASE}/api/applications/${appA.id}/documents`)).json()).length;
await fetch(`${BASE}/api/applications/${appA.id}`, { method: 'DELETE' });
check(storedFiles().length === filesBefore - docCountA, `deleting application removed its ${docCountA} files, kept others`);
check(Buffer.from(await (await fetch(`${BASE}/api/documents/${v2.id}/download`)).arrayBuffer()).equals(makePdf('Version November')), 'other application\'s document untouched');

// Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.goto(`${BASE}/bewerbungen.html?id=${appB.id}`);
await page.waitForSelector('dialog.side-panel[open]');
await page.click('#tab-btn-documents');
check(!(await page.evaluate(() => document.querySelector('.side-panel').scrollWidth > window.innerWidth)), 'mobile: documents tab fits the screen');
await page.screenshot({ path: path.join(SHOTS, 'm4-04-mobile.png') });

// M2 regression for the [hidden] fix: reset button must be hidden when no filter is active
await page.keyboard.press('Escape');
check(!(await page.isVisible('#filter-reset')), 'list: "Filter zurücksetzen" hidden without active filters');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
// The built-in PDF viewer can leave a pending frame navigation; ignore errors while shutting down.
await browser.close().catch(() => {});
