// Screenshots for the README (docs/screenshots/*.png), taken from a DEMO instance only – they
// must never show real data. Not part of run.mjs.
//
// Usage: start the demo   dotnet run --project src/BewerbungsPilot --launch-profile demo
//        then (tests/e2e)  node screenshots.mjs            (BASE defaults to http://localhost:5216)
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.BASE ?? 'http://localhost:5216';
const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const OUT = path.resolve(DIR, '../../docs/screenshots');

const options = await (await fetch(BASE + '/api/auth/antiforgery')).json();
if (!options.demoEnabled) {
  console.error('Abbruch: Die Anwendung unter ' + BASE + ' läuft nicht im Demo-Modus. Screenshots nur mit fiktiven Daten.');
  process.exit(1);
}
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({ channel: 'msedge' });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, locale: 'de-DE' });
const page = await context.newPage();

await page.goto(BASE + '/login.html');
await page.waitForSelector('#demo-section:not([hidden])');
await page.screenshot({ path: path.join(OUT, '00-anmeldung.png') });
await page.click('#demo-start');
await page.waitForURL((url) => url.pathname === '/' || url.pathname === '/index.html');

const shots = [
  ['01-uebersicht.png', '/', '#status-chart'],
  ['02-bewerbungen.png', '/bewerbungen.html', 'tr[data-id]'],
  ['03-pipeline.png', '/pipeline.html', '#board [data-id]'],
  ['04-kalender.png', '/kalender.html', '#calendar-grid'],
  ['05-verlauf.png', '/verlauf.html', '#tl-body'],
  ['06-analyse.png', '/analyse.html', '#an-milestones'],
  ['07-standorte.png', '/standorte.html', '#place-list'],
];
for (const [file, url, selector] of shots) {
  await page.goto(BASE + url);
  await page.waitForSelector(selector);
  await page.waitForTimeout(1200); // chart and map animations, map tiles
  await page.screenshot({ path: path.join(OUT, file) });
  console.log('gespeichert: docs/screenshots/' + file);
}

// Detail panel of the application with documents and interviews.
await page.goto(BASE + '/bewerbungen.html');
await page.locator('tr[data-id]', { hasText: 'Elbstrom' }).click();
await page.waitForSelector('dialog.side-panel[open]');
await page.waitForTimeout(600);
await page.screenshot({ path: path.join(OUT, '08-detail.png') });
console.log('gespeichert: docs/screenshots/08-detail.png');

await browser.close();
