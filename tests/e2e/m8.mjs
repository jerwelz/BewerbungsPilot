import { chromium } from 'playwright';
import { createSession } from './session.mjs';
import http from 'node:http';
import path from 'node:path';

const BASE = 'http://localhost:5299';
// M9: the suite works as its own registered user; fetch sends the login cookie and CSRF token.
const session = await createSession();
const fetch = session.fetch;
const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const SHOTS = path.join(DIR, 'shots');
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

// ---------- Fake Nominatim (the app is started with --Geocoding:BaseUrl http://localhost:5399/) ----------
const geocodeRequests = [];
const PLACES = {
  altona: [{ lat: '53.5503', lon: '9.9350', display_name: 'Altona, Hamburg, Deutschland', addresstype: 'suburb' }],
  berlin: [{ lat: '52.5200', lon: '13.4050', display_name: 'Berlin, Deutschland', addresstype: 'city' }],
  'hafenstraße 12, hamburg': [{ lat: '53.5446', lon: '9.9661', display_name: 'Hafenstraße 12, St. Pauli, Hamburg, Deutschland', addresstype: 'building' }],
};
const fake = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const q = (url.searchParams.get('q') ?? '').toLowerCase();
  geocodeRequests.push({ q, at: Date.now(), ua: req.headers['user-agent'], format: url.searchParams.get('format') });
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(PLACES[q] ?? []));
}).listen(5399);

// ---------- seed ----------
const json = (r) => r.json();
const send = (method, url, body) => fetch(BASE + url, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
const create = (company, status, location) => send('POST', '/api/applications', {
  companyName: company, position: 'Auszubildender FIAE', status, location, notes: 'Geheime Notiz',
}).then(json);
const setLocation = (id, body) => send('PUT', `/api/applications/${id}/map-location`, body);

const A = await create('Nordlicht Software GmbH', 'Beworben', 'Hamburg');
const B = await create('Hafen Logistik AG', 'InPruefung', 'Hamburg, Hafen');
const C = await create('Berlin Data KG', 'Vorstellungsgespraech', 'Berlin');
const D = await create('Remote Werk AG', 'Beworben', 'Remote');
const E = await create('Altona Digital GmbH', 'Entwurf', 'Hamburg-Altona');
const F = await create('Lübeck Software', 'Absage', 'Lübeck');
await setLocation(A.id, { precision: 'Exact', latitude: 53.5446, longitude: 9.9661, label: 'Hafenstraße 12, Hamburg' });
await setLocation(B.id, { precision: 'Exact', latitude: 53.5446, longitude: 9.9661, label: 'Hafenstraße 12, Hamburg' });
await setLocation(C.id, { precision: 'City', latitude: 52.52, longitude: 13.405, label: 'Berlin' });
await setLocation(D.id, { precision: 'Remote', latitude: null, longitude: null, label: 'Remote (Deutschland)' });

// ---------- API checks ----------
let r = await setLocation(A.id, { precision: 'Exact', latitude: 95, longitude: 10, label: null });
check(r.status === 400 && (await r.json()).errors.latitude[0].includes('Breitengrad'), 'API: latitude 95 → 400 German');
r = await setLocation(A.id, { precision: 'City', latitude: null, longitude: null, label: 'x' });
check(r.status === 400, 'API: city without coordinates → 400');
r = await setLocation(9999, { precision: 'City', latitude: 53, longitude: 10, label: null });
check(r.status === 404, 'API: unknown application → 404');
r = await send('POST', '/api/map/query', { origin: { latitude: 200, longitude: 10 } });
check(r.status === 400, 'API: invalid origin → 400');
const withOrigin = await send('POST', '/api/map/query', { origin: { latitude: 53.5511, longitude: 9.9937 } }).then(json);
const cItem = withOrigin.items.find((i) => i.id === C.id);
check(cItem.distanceKm >= 254 && cItem.distanceKm <= 257, `API: Hamburg → Berlin ${cItem.distanceKm} km (Luftlinie)`);
check(withOrigin.items.find((i) => i.id === E.id).distanceKm === null && withOrigin.items.find((i) => i.id === E.id).latitude === null, 'API: no position → no distance, no coordinates');
check(withOrigin.items.filter((i) => i.sameLocationCount === 2).length === 2, 'API: two applications share one position');
check(!JSON.stringify(withOrigin).includes('Geheime Notiz'), 'API: notes not exposed');
check(cItem.links.transit.startsWith('https://www.google.com/maps/dir/?api=1&origin=53.5511%2C9.9937&destination=52.52%2C13.405&travelmode=transit'), 'API: documented Google Maps directions URL');
check(withOrigin.items.find((i) => i.id === D.id).links === null, 'API: no route for remote');
r = await send('POST', '/api/geocode', { query: 'ab' });
check(r.status === 400, 'API: geocode query too short → 400');
check(geocodeRequests.length === 0, 'nothing sent to the geocoder without explicit search');

// ---------- Browser ----------
const browser = await chromium.launch({ channel: 'msedge' });
const context = await session.newContext(browser, { viewport: { width: 1440, height: 900 } });
let tileRequests = 0;
const EMPTY_PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');
await context.route('https://tile.openstreetmap.org/**', (route) => { tileRequests++; route.fulfill({ status: 200, contentType: 'image/png', body: EMPTY_PNG }); });
const page = await context.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/status of (400|404|500|502)/.test(m.text())) errors.push(m.text()); });

await page.goto(BASE + '/standorte.html');
await page.waitForSelector('.map-pin');
check(await page.locator('.map-pin').count() === 2, 'two markers (A+B share one position, C)');
check((await page.locator('.map-pin', { hasText: '2' }).count()) === 1, 'shared marker shows "2"');
check(await page.locator('.place-section').count() === 3, 'list sections: map, remote, without position');
check((await page.textContent('#place-list')).includes('Ohne bestätigte Kartenposition'), 'unconfirmed locations listed separately');
check(tileRequests > 0 && (await page.textContent('.leaflet-control-attribution')).includes('OpenStreetMap'), 'tiles requested + OSM attribution shown');
check(geocodeRequests.length === 0, 'page load sends nothing to the geocoder');
await page.screenshot({ path: path.join(SHOTS, 'm8-01-map.png') });

// List → map
await page.click(`[data-select="${C.id}"]`);
await page.waitForSelector('.leaflet-popup .map-popup-company:has-text("Berlin Data KG")');
check(await page.locator('.map-pin.is-selected').count() === 1, 'list selection highlights marker');
await page.waitForTimeout(900);
const center = await page.evaluate(() => {
  const el = document.querySelector('.map-pin.is-selected').getBoundingClientRect();
  const m = document.getElementById('map').getBoundingClientRect();
  return Math.hypot(el.x + el.width / 2 - (m.x + m.width / 2), el.y + el.height / 2 - (m.y + m.height / 2));
});
check(center < 40, `map centered on selection (offset ${Math.round(center)} px)`);

// Map → list (keyboard: markers are focusable buttons; Hamburg is outside the Berlin view, so no mouse click)
await page.locator('.leaflet-marker-icon', { has: page.locator('.map-pin', { hasText: '2' }) }).focus();
await page.keyboard.press('Enter');
await page.waitForSelector('.leaflet-popup .map-popup-company:has-text("Hafen Logistik AG")');
// The previous popup may still be fading out – read the one that belongs to this marker.
const popupText = await page.locator('.leaflet-popup', { hasText: 'Hafen Logistik AG' }).textContent();
check(popupText.includes('Nordlicht') && popupText.includes('Hafen Logistik'), 'shared marker popup lists both applications');
check(await page.locator('.place-item.is-selected').count() === 1, 'marker selection highlights one list entry');
check([A.id, B.id].includes(Number(await page.getAttribute('.place-item.is-selected', 'data-id'))), 'highlighted entry belongs to the marker');
check(await page.evaluate(() => document.activeElement?.classList.contains('leaflet-marker-icon')), 'keyboard focus stays on the marker');
check(await page.locator('.map-pin.is-selected', { hasText: '2' }).count() === 1, 'shared marker now highlighted');
await page.locator('.leaflet-popup [data-open]').first().click();
await page.waitForSelector('dialog.side-panel[open]');
check(true, 'popup "Details" opens application');
await page.keyboard.press('Escape');
await page.waitForSelector('dialog.side-panel', { state: 'hidden' });
await page.screenshot({ path: path.join(SHOTS, 'm8-02-selected.png') });

// Origin via search
await page.click('[data-origin-edit]');
await page.fill('#p-query', 'Altona');
await page.click('[data-search]');
await page.waitForSelector('.place-result');
check((await page.textContent('[data-attribution]')).includes('OpenStreetMap'), 'geocoding attribution shown');
check(geocodeRequests.length === 1 && geocodeRequests[0].ua.startsWith('BewerbungsPilot/1.0') && geocodeRequests[0].format === 'jsonv2', 'one request with identifying User-Agent');
await page.check('.place-result input');
check(await page.inputValue('#p-latitude') === '53.5503', 'result fills coordinates');
await page.click('[data-search]'); // same text again → cache
await page.waitForSelector('.place-result');
check(geocodeRequests.length === 1, 'repeated search served from cache');
await page.check('.place-result input');
await page.click('.place-dialog [data-submit]');
await page.waitForSelector('.origin-text:has-text("Altona")');
// M9: the starting point is stored per user (key bewerbungspilot.origin.<userId>).
const ORIGIN_KEY = `bewerbungspilot.origin.${session.user.id}`;
check(await page.evaluate((key) => JSON.parse(localStorage.getItem(key)).latitude, ORIGIN_KEY) === 53.5503, 'origin stored in localStorage');
check(await page.evaluate(() => localStorage.getItem('bewerbungspilot.origin')) === null, 'origin not stored under the shared pre-M9 key');
check((await page.textContent('#place-list')).includes('km Luftlinie'), 'distances labeled "Luftlinie"');
check(!(await page.textContent('#place-list')).replaceAll('Keine Fahrzeitberechnung', '').match(/Fahrzeit|Fahrstrecke|Minuten|\bStd\b/),
  'no travel-time claims (only the explicit "Keine Fahrzeitberechnung" note)');
await page.selectOption('#map-sort', 'distance');
let order = await page.locator('.place-section').first().locator('.place-company').allTextContents();
check(order.at(-1) === 'Berlin Data KG', `distance sort: Berlin last (${order.join(', ')})`);
const berlinKm = await page.locator(`.place-item[data-id="${C.id}"] .distance strong`).textContent();
check(/^25\d,\d km$/.test(berlinKm), `Altona → Berlin shown as ${berlinKm}`);

// Change origin to Berlin with manual coordinates
await page.click('[data-origin-edit]');
await page.fill('#p-latitude', '52,52');
await page.fill('#p-longitude', '13,405');
await page.fill('#p-label', 'Berlin (Umzug?)');
await page.click('.place-dialog [data-submit]');
await page.waitForSelector('.origin-text:has-text("Berlin (Umzug?)")');
order = await page.locator('.place-section').first().locator('.place-company').allTextContents();
check(order[0] === 'Berlin Data KG' && (await page.locator(`.place-item[data-id="${C.id}"] .distance strong`).textContent()) === '0,0 km', 'origin change re-sorts (Berlin first, 0,0 km)');

// Rate limit: two different searches are spaced ≥ 1 s apart on the server side
await page.click('[data-origin-edit]');
await page.fill('#p-query', 'Berlin');
await page.click('[data-search]');
await page.waitForSelector('.place-result');
await page.fill('#p-query', 'Hafenstraße 12, Hamburg');
await page.click('[data-search]');
await page.waitForSelector('.place-result-name:has-text("St. Pauli")');
const gap = geocodeRequests.at(-1).at - geocodeRequests.at(-2).at;
check(gap >= 1000, `requests to geocoder spaced ${gap} ms (≥ 1000)`);
await page.click('.place-dialog [data-close]');

// Remove origin
await page.click('[data-origin-remove]');
await page.waitForSelector('.origin-text:has-text("Kein Startpunkt")');
await page.waitForFunction(() => !document.querySelector('#place-list').textContent.includes('Luftlinie'));
check(!(await page.textContent('#place-list')).includes('Luftlinie') && await page.isDisabled('#map-sort option[value="distance"]'), 'origin removed: no distances, distance sort disabled');
check(await page.evaluate((key) => localStorage.getItem(key), ORIGIN_KEY) === null, 'origin removed from localStorage');

// Confirm a position via search (Altona Digital)
await page.click(`[data-locate="${E.id}"]`);
check(await page.inputValue('#p-query') === 'Hamburg-Altona', 'dialog pre-fills typed location');
await page.fill('#p-query', 'Altona');
await page.click('[data-search]');
await page.waitForSelector('.place-result');
await page.check('.place-result input');
check(await page.inputValue('#p-precision') === 'Approximate', 'suburb result suggests "ungefähr"');
await page.click('.place-dialog [data-submit]');
await page.waitForFunction(() => document.querySelectorAll('.map-pin').length === 3);
check(true, 'confirmed position appears as new marker');

// Manual entry with an invalid value first (Lübeck)
await page.click(`[data-locate="${F.id}"]`);
await page.selectOption('#p-precision', 'City');
await page.fill('#p-latitude', '95');
await page.fill('#p-longitude', '10,6866');
await page.click('.place-dialog [data-submit]');
await page.waitForSelector('#p-latitude-error:has-text("Breitengrad")');
check(true, 'invalid latitude → German server error in dialog');
await page.fill('#p-latitude', '53,8655');
await page.fill('#p-label', 'Lübeck');
await page.click('.place-dialog [data-submit]');
await page.waitForFunction(() => document.querySelectorAll('.map-pin').length === 4);
check(!(await page.textContent('#place-list')).includes('Ohne bestätigte Kartenposition'), 'all non-remote applications now on the map');

// Filters
await page.fill('#map-search', 'berlin');
check(await page.locator('.place-item').count() === 1 && await page.locator('.map-pin').count() === 1, 'search filters list and markers');
await page.fill('#map-search', '');
await page.selectOption('#map-precision', 'Remote');
check(await page.locator('.place-item').count() === 1 && (await page.textContent('.place-item')).includes('Remote Werk'), 'location filter: Remote');
await page.selectOption('#map-precision', 'Exact');
check(await page.locator('.place-item').count() === 2, 'location filter: exact addresses (2)');
await page.selectOption('#map-precision', 'all');
await page.selectOption('#map-status', 'Absage');
check(await page.locator('.place-item').count() === 1, 'status filter');
await page.selectOption('#map-status', '');
await page.fill('#map-search', 'gibt es nicht');
check(await page.isVisible('[data-reset]'), 'empty state with reset');
await page.click('[data-reset]');

// Route links
const item = page.locator(`.place-item[data-id="${C.id}"]`);
await item.locator('.route-menu summary').click();
const transit = item.locator('.route-links a', { hasText: 'ÖPNV' });
check((await transit.getAttribute('href')).includes('travelmode=transit') && await transit.getAttribute('target') === '_blank'
  && (await transit.getAttribute('rel')).includes('noopener'), 'route link: transit, new tab, noopener');
check((await item.textContent()).includes('Keine Fahrzeitberechnung'), 'route note: no travel time calculated');
await page.screenshot({ path: path.join(SHOTS, 'm8-03-list.png') });

// Mobile
await page.setViewportSize({ width: 375, height: 812 });
await page.reload();
await page.waitForSelector('.map-pin');
check(await page.isVisible('#map') && !(await page.isVisible('.place-panel')), 'mobile: map view by default, list hidden');
check(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'mobile: no horizontal overflow');
await page.screenshot({ path: path.join(SHOTS, 'm8-04-mobile-map.png') });
await page.click('button[data-view="list"]');
check(await page.isVisible('.place-panel') && !(await page.isVisible('#map')), 'mobile: toggle to list');
await page.screenshot({ path: path.join(SHOTS, 'm8-05-mobile-list.png') });
await page.click(`[data-select="${C.id}"]`);
await page.waitForSelector('.leaflet-popup .map-popup-company:has-text("Berlin Data KG")');
check(await page.isVisible('#map'), 'mobile: selecting in list switches to map');

check(errors.length === 0, `no console errors ${errors.length ? JSON.stringify(errors) : ''}`);
await browser.close();
fake.close();
