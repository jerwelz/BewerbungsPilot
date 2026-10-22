// M9 session expiration in real time. run.mjs starts the app with Auth:SessionLifetime = 15 s.
import { chromium } from 'playwright';
import { createSession, BASE, PASSWORD } from './session.mjs';

const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };
const session = await createSession();
const created = await session.fetch(BASE + '/api/applications', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ companyName: 'Ablauf Test GmbH', position: 'Azubi', status: 'Beworben', notes: 'Notiz vor dem Ablauf' }),
}).then((r) => r.json());

const browser = await chromium.launch({ channel: 'msedge' });
const context = await session.newContext(browser, { viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
await page.goto(BASE + '/bewerbungen.html');
await page.waitForSelector('tr[data-id]');

// Activity keeps the session alive (sliding expiration): 3 requests 8 s apart = 24 s > 15 s.
for (let i = 0; i < 3; i++) {
  await page.waitForTimeout(8000);
  await page.locator('tr[data-id]').first().click();
  await page.waitForSelector('dialog.side-panel[open]');
  await page.keyboard.press('Escape');
  await page.waitForSelector('dialog.side-panel[open]', { state: 'detached' }).catch(() => {});
}
check(page.url().includes('/bewerbungen.html'), 'active for 24 s with 15 s lifetime: still logged in (sliding expiration)');

// 16 s without any request → the next action leads to the login page.
await page.waitForTimeout(16000);
await page.locator('tr[data-id]').first().click();
await page.waitForURL(/\/login\.html/, { timeout: 8000 });
check(page.url().includes('expired=1'), 'inactive for 16 s: next action → login page');
check(await page.waitForSelector('#auth-notice:has-text("Sitzung ist abgelaufen")', { timeout: 5000 }).then(() => true, () => false), 'German "session expired" message');
check(!(await page.locator('body').innerText()).includes('Notiz vor dem Ablauf'), 'no private data left on screen');

await page.fill('#a-email', session.email);
await page.fill('#a-password', PASSWORD);
await page.click('#login-form button[type="submit"]');
await page.waitForURL(/\/bewerbungen\.html/, { waitUntil: 'commit' });
await page.waitForSelector(`tr[data-id="${created.id}"]`);
check(true, 'after logging in again the user is back on the same page with the data');
await browser.close();
