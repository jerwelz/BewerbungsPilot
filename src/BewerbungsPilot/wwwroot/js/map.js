// Page script for standorte.html: Leaflet map + synchronized company list.
//
// Data comes from POST /api/map/query (MapService.cs): confirmed positions, straight-line
// distances and route links. Coordinates are never guessed here – applications without a
// confirmed position are listed separately.

import { api } from './api.js';
import { renderSidebar, STATUSES, statusLabel, statusBadge, escapeHtml, matchesSearch, showToast } from './ui.js';
import { openDetailPanel } from './detail-panel.js';
import { openPlaceDialog, PRECISIONS } from './place-dialog.js';
import { requireUser, originKey } from './session.js';

renderSidebar('places');

let originStorageKey = null; // per user, set once the login is confirmed (see the end of this file)
const GERMANY = [51.16, 10.45];
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const mobile = window.matchMedia('(max-width: 860px)');

const PRECISION_SHORT = { Exact: 'Genaue Adresse', Approximate: 'Ungefähr', City: 'Stadt / Ort', Remote: 'Remote', Unknown: 'Ohne Position' };
const ROUTES = [['transit', 'ÖPNV'], ['driving', 'Auto'], ['bicycling', 'Fahrrad'], ['walking', 'Zu Fuß']];

const state = { items: [], hasOrigin: false, origin: null, selectedId: null, search: '', status: '', precision: 'all', sort: 'company' };

const el = {
  layout: document.getElementById('map-layout'),
  list: document.getElementById('place-list'),
  originBar: document.getElementById('origin-bar'),
  error: document.getElementById('map-error'),
  search: document.getElementById('map-search'),
  status: document.getElementById('map-status'),
  precision: document.getElementById('map-precision'),
  sort: document.getElementById('map-sort'),
};

// ---------- Starting point (stored only in this browser) ----------

function loadOrigin() {
  try {
    const stored = JSON.parse(localStorage.getItem(originStorageKey));
    return stored && Number.isFinite(stored.latitude) && Number.isFinite(stored.longitude) ? stored : null;
  } catch {
    return null;
  }
}

function saveOrigin(origin) {
  state.origin = origin;
  try {
    if (origin) localStorage.setItem(originStorageKey, JSON.stringify(origin));
    else localStorage.removeItem(originStorageKey);
  } catch {
    showToast('Der Startpunkt konnte in diesem Browser nicht gespeichert werden.', 'error');
  }
}

// Same rules as GeoMath.ValidateCoordinates on the server (which checks again).
function validateOrigin(lat, lon) {
  if (lat === null || lon === null) return 'Bitte geben Sie Breiten- und Längengrad an oder wählen Sie einen Suchtreffer.';
  if (!Number.isFinite(lat) || lat < -90 || lat > 90) return 'Der Breitengrad muss zwischen −90 und 90 liegen.';
  if (!Number.isFinite(lon) || lon < -180 || lon > 180) return 'Der Längengrad muss zwischen −180 und 180 liegen.';
  if (lat === 0 && lon === 0) return 'Die Koordinaten 0/0 liegen im Atlantik – vermutlich fehlen die Werte.';
  return null;
}

function renderOriginBar() {
  const o = state.origin;
  el.originBar.innerHTML = o
    ? `<div class="origin-text">
         <span class="origin-icon" aria-hidden="true"></span>
         <span><strong>Startpunkt:</strong> ${escapeHtml(o.label)}
           <span class="muted">(${o.latitude.toFixed(4)}, ${o.longitude.toFixed(4)} · nur in diesem Browser gespeichert)</span></span>
       </div>
       <div class="origin-actions">
         <button type="button" class="btn btn-secondary btn-sm" data-origin-edit>Ändern</button>
         <button type="button" class="btn btn-ghost btn-sm" data-origin-remove>Entfernen</button>
       </div>`
    : `<div class="origin-text"><span class="origin-icon is-empty" aria-hidden="true"></span>
         <span>Kein Startpunkt festgelegt. Mit einem Startpunkt (z. B. Wohnort oder ein Ort, an den Sie umziehen könnten)
         sehen Sie Entfernungen in Luftlinie.</span></div>
       <div class="origin-actions"><button type="button" class="btn btn-primary btn-sm" data-origin-edit>Startpunkt festlegen</button></div>`;
  el.sort.querySelector('[value="distance"]').disabled = !o;
  if (!o && state.sort === 'distance') {
    state.sort = 'company';
    el.sort.value = 'company';
  }
}

el.originBar.addEventListener('click', (event) => {
  if (event.target.closest('[data-origin-remove]')) {
    saveOrigin(null);
    showToast('Startpunkt entfernt');
    load();
  }
  if (event.target.closest('[data-origin-edit]')) {
    openPlaceDialog({
      title: state.origin ? 'Startpunkt ändern' : 'Startpunkt festlegen',
      intro: 'Eine genaue Wohnadresse ist nicht nötig – ein Ort oder Stadtteil genügt.',
      withPrecision: false,
      initial: state.origin ? { ...state.origin, query: state.origin.label } : {},
      onSave: async ({ latitude, longitude, label }) => {
        const message = validateOrigin(latitude, longitude);
        if (message) throw { errors: { latitude: [message] }, message };
        saveOrigin({ latitude, longitude, label: label ?? 'Startpunkt' });
        showToast('Startpunkt gespeichert');
        await load();
      },
      onRemove: state.origin ? () => {
        saveOrigin(null);
        load();
      } : null,
    });
  }
});

// ---------- Map ----------

const map = L.map('map', { zoomControl: true }).setView(GERMANY, 6);
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19,
  // The OSM tile policy requires a Referer; the page's own policy ("same-origin") would drop it.
  // Only the origin is sent (e.g. https://bewerbungspilot.fly.dev/), never the page path.
  referrerPolicy: 'strict-origin-when-cross-origin',
  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>-Mitwirkende',
}).on('tileerror', () => { document.getElementById('tile-notice').hidden = false; })
  .addTo(map);

const markerLayer = L.layerGroup().addTo(map);
let markers = new Map(); // position key → { marker, items }
let originMarker = null;
let firstFit = true;

const positionKey = (item) => `${item.latitude.toFixed(5)}|${item.longitude.toFixed(5)}`;

function pinIcon(group, selected) {
  const statuses = new Set(group.map((i) => i.status));
  const accent = statuses.size === 1 ? group[0].status : 'Mixed';
  const uncertain = group.every((i) => i.precision !== 'Exact');
  return L.divIcon({
    className: 'map-pin-wrapper',
    html: `<span class="map-pin accent-${accent}${uncertain ? ' is-approximate' : ''}${selected ? ' is-selected' : ''}">${group.length > 1 ? group.length : ''}</span>`,
    iconSize: [26, 26],
    iconAnchor: [13, 13],
    popupAnchor: [0, -14],
  });
}

function renderMarkers(visible) {
  markerLayer.clearLayers();
  markers = new Map();
  const groups = new Map();
  for (const item of visible.filter((i) => i.latitude !== null)) {
    const key = positionKey(item);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(item);
  }
  for (const [key, group] of groups) {
    const selected = group.some((i) => i.id === state.selectedId);
    // title = accessible name for keyboard and screen reader users (Leaflet makes markers focusable)
    const title = group.length === 1
      ? `${group[0].companyName} – ${statusLabel(group[0].status)} (${PRECISION_SHORT[group[0].precision]})`
      : `${group.length} Bewerbungen an diesem Standort: ${group.map((i) => i.companyName).join(', ')}`;
    const marker = L.marker([group[0].latitude, group[0].longitude], { icon: pinIcon(group, selected), title, alt: title, riseOnHover: true })
      .bindPopup(() => popupHtml(group), { maxWidth: 300 })
      // "popupopen" fires for mouse clicks AND for Enter on a focused marker (Leaflet's keyboard
      // support only opens the popup – it fires no "click"). Already selected → nothing to do.
      .on('popupopen', () => {
        if (!group.some((i) => i.id === state.selectedId)) select(group[0].id, { from: 'map' });
      });
    marker.addTo(markerLayer);
    markers.set(key, { marker, items: group });
  }

  if (originMarker) originMarker.remove();
  originMarker = state.origin
    ? L.marker([state.origin.latitude, state.origin.longitude], {
      icon: L.divIcon({ className: 'map-pin-wrapper', html: '<span class="origin-pin"></span>', iconSize: [22, 22], iconAnchor: [11, 11] }),
      title: `Startpunkt: ${state.origin.label}`,
      alt: `Startpunkt: ${state.origin.label}`,
      zIndexOffset: 1000,
    }).bindTooltip(`Startpunkt: ${escapeHtml(state.origin.label)}`).addTo(map)
    : null;
}

function fitToMarkers() {
  const points = [...markers.values()].map(({ marker }) => marker.getLatLng());
  if (state.origin) points.push(L.latLng(state.origin.latitude, state.origin.longitude));
  if (points.length === 1) map.setView(points[0], 12);
  else if (points.length > 1) map.fitBounds(L.latLngBounds(points), { padding: [36, 36], maxZoom: 13 });
}

function popupHtml(group) {
  return `<div class="map-popup">${group.map((i) => `
    <div class="map-popup-item${i.id === state.selectedId ? ' is-selected' : ''}">
      <div class="map-popup-company">${escapeHtml(i.companyName)}</div>
      <div class="map-popup-position">${escapeHtml(i.position)}</div>
      <div class="map-popup-meta">${statusBadge(i.status)} <span class="precision-chip">${PRECISION_SHORT[i.precision]}</span></div>
      ${i.mapLabel ? `<div class="map-popup-position">${escapeHtml(i.mapLabel)}</div>` : ''}
      ${distanceHtml(i)}
      <div class="map-popup-actions">
        <button type="button" class="btn btn-secondary btn-sm" data-open="${i.id}">Details</button>
        ${routesHtml(i)}
      </div>
    </div>`).join('')}</div>`;
}

// ---------- List ----------

const STATUS_ORDER = STATUSES.map((s) => s.value);
const SORTERS = {
  company: (a, b) => a.companyName.localeCompare(b.companyName, 'de', { sensitivity: 'base' }),
  // Without distance (no position) at the end – never treated as 0 km.
  distance: (a, b) => (a.distanceKm === null) - (b.distanceKm === null) || (a.distanceKm ?? 0) - (b.distanceKm ?? 0),
  status: (a, b) => STATUS_ORDER.indexOf(a.status) - STATUS_ORDER.indexOf(b.status) || SORTERS.company(a, b),
};

function matchesPrecision(item) {
  if (state.precision === 'all') return true;
  if (state.precision === 'mapped') return item.latitude !== null;
  return item.precision === state.precision;
}

function visibleItems() {
  return state.items
    .filter((i) => matchesSearch({ companyName: i.companyName, position: i.position }, state.search))
    .filter((i) => !state.status || i.status === state.status)
    .filter(matchesPrecision)
    .sort(SORTERS[state.sort]);
}

const formatKm = (km) => km.toLocaleString('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 1 });

function distanceHtml(item) {
  if (item.distanceKm === null) return '';
  return `<div class="distance"><strong>${formatKm(item.distanceKm)} km</strong> Luftlinie</div>`;
}

function routesHtml(item) {
  if (!item.links) return '';
  return `<details class="route-menu">
      <summary class="btn btn-ghost btn-sm">Route</summary>
      <div class="route-links">
        ${ROUTES.map(([key, label]) => `<a href="${escapeHtml(item.links[key])}" target="_blank" rel="noopener noreferrer">${label}</a>`).join('')}
        <p class="route-note">Öffnet Google Maps${state.origin ? ' ab Ihrem Startpunkt' : ''}. Keine Fahrzeitberechnung durch BewerbungsPilot.</p>
      </div>
    </details>`;
}

function itemHtml(item) {
  const place = item.mapLabel ?? item.location;
  return `
    <li class="place-item${item.id === state.selectedId ? ' is-selected' : ''}" data-id="${item.id}">
      <button type="button" class="place-select" data-select="${item.id}" aria-pressed="${item.id === state.selectedId}">
        <span class="place-company">${escapeHtml(item.companyName)}</span>
        <span class="place-position">${escapeHtml(item.position)}</span>
      </button>
      <div class="place-meta">
        ${statusBadge(item.status)}
        <span class="precision-chip precision-${item.precision}">${PRECISION_SHORT[item.precision]}</span>
        ${item.sameLocationCount > 1 ? `<span class="precision-chip">${item.sameLocationCount} am selben Ort</span>` : ''}
      </div>
      ${place ? `<div class="place-location">${escapeHtml(place)}</div>` : ''}
      ${distanceHtml(item)}
      <div class="place-actions">
        <button type="button" class="btn btn-ghost btn-sm" data-open="${item.id}">Details</button>
        <button type="button" class="btn btn-ghost btn-sm" data-locate="${item.id}">
          ${item.precision === 'Unknown' ? 'Standort festlegen' : 'Standort ändern'}</button>
        ${routesHtml(item)}
      </div>
    </li>`;
}

function renderList(visible) {
  if (state.items.length === 0) {
    el.list.innerHTML = '<p class="tl-state">Noch keine Bewerbungen. Legen Sie Bewerbungen an, um ihre Standorte zu sehen.</p>';
    return;
  }
  if (visible.length === 0) {
    el.list.innerHTML = `<div class="tl-state"><p>Keine Bewerbungen passen zu Suche und Filtern.</p>
      <button type="button" class="btn btn-secondary btn-sm" data-reset>Filter zurücksetzen</button></div>`;
    return;
  }

  const section = (title, items, hint) => items.length === 0 ? '' : `
    <section class="place-section">
      <h2 class="place-section-title">${title} <span class="tab-count">${items.length}</span></h2>
      ${hint ? `<p class="place-hint">${hint}</p>` : ''}
      <ul class="place-list">${items.map(itemHtml).join('')}</ul>
    </section>`;

  el.list.innerHTML =
    section('Auf der Karte', visible.filter((i) => i.latitude !== null)) +
    section('Remote', visible.filter((i) => i.precision === 'Remote')) +
    section('Ohne bestätigte Kartenposition', visible.filter((i) => i.precision === 'Unknown'),
      'Diese Bewerbungen erscheinen erst nach Bestätigung eines Standorts auf der Karte – Koordinaten werden nicht geraten.');
}

function render() {
  const visible = visibleItems();
  renderList(visible);
  renderMarkers(visible);
  if (firstFit && markers.size + (state.origin ? 1 : 0) > 0) {
    fitToMarkers();
    firstFit = false;
  }
}

// ---------- Selection (list ↔ map) ----------

function select(id, { from }) {
  const previous = state.selectedId;
  state.selectedId = id;
  const item = state.items.find((i) => i.id === id);

  // List: move the highlight without rebuilding the list.
  el.list.querySelectorAll('.place-item').forEach((li) => {
    const selected = Number(li.dataset.id) === id;
    li.classList.toggle('is-selected', selected);
    li.querySelector('[data-select]').setAttribute('aria-pressed', String(selected));
  });
  if (from === 'map') {
    el.list.querySelector(`.place-item[data-id="${id}"]`)?.scrollIntoView({ block: 'nearest', behavior: reducedMotion ? 'auto' : 'smooth' });
  }

  // Map: move the highlight between markers. Only a class is toggled – replacing the icon
  // element would take the keyboard focus away from the marker.
  for (const entry of markers.values()) {
    if (entry.items.some((i) => i.id === id || i.id === previous)) {
      entry.marker.getElement()?.querySelector('.map-pin')?.classList.toggle('is-selected', entry.items.some((i) => i.id === id));
    }
  }
  if (item?.latitude != null) {
    const entry = markers.get(positionKey(item));
    if (from === 'list') {
      if (mobile.matches) setView('map');
      const target = [item.latitude, item.longitude];
      const zoom = Math.max(map.getZoom(), 12);
      if (reducedMotion) map.setView(target, zoom);
      else map.flyTo(target, zoom, { duration: 0.6 });
    }
    entry?.marker.setPopupContent(popupHtml(entry.items)).openPopup();
  } else {
    map.closePopup();
  }
}

// ---------- Editing a position ----------

function editLocation(id) {
  const item = state.items.find((i) => i.id === id);
  openPlaceDialog({
    title: `Standort: ${item.companyName}`,
    intro: item.location ? `Eingetragener Ort: „${item.location}“. Bitte bestätigen Sie die Position auf der Karte.` : 'Für diese Bewerbung ist kein Ort eingetragen.',
    withPrecision: true,
    initial: {
      precision: item.precision,
      latitude: item.latitude,
      longitude: item.longitude,
      label: item.mapLabel ?? item.location ?? '',
      query: item.mapLabel ?? item.location ?? '',
    },
    onSave: async (value) => {
      await api.setMapLocation(id, value); // throws ApiError with German messages if invalid
      showToast('Standort gespeichert');
      state.selectedId = id;
      await load();
      select(id, { from: 'list' });
    },
    onRemove: item.precision === 'Unknown' ? null : async () => {
      await api.setMapLocation(id, { precision: 'Unknown', latitude: null, longitude: null, label: null });
      showToast('Kartenposition entfernt');
      await load();
    },
  });
}

// ---------- Events ----------

function handleActions(event) {
  const open = event.target.closest('[data-open]');
  if (open) openDetailPanel(Number(open.dataset.open), { onChanged: load, onDeleted: load });
  const locate = event.target.closest('[data-locate]');
  if (locate) editLocation(Number(locate.dataset.locate));
  const choose = event.target.closest('[data-select]');
  if (choose) select(Number(choose.dataset.select), { from: 'list' });
  if (event.target.closest('[data-reset]')) resetFilters();
}
el.list.addEventListener('click', handleActions);
// Leaflet stops clicks inside popups from bubbling up, so each opened popup gets the listener itself.
map.on('popupopen', (event) => event.popup.getElement().addEventListener('click', handleActions));

function setView(view) {
  el.layout.dataset.view = view;
  document.querySelectorAll('[data-view]').forEach((b) => {
    if (b.tagName === 'BUTTON') b.setAttribute('aria-pressed', String(b.dataset.view === view));
  });
  if (view === 'map') setTimeout(() => map.invalidateSize(), 0); // the map was hidden – recalculate its size
}
document.querySelectorAll('button[data-view]').forEach((b) => b.addEventListener('click', () => setView(b.dataset.view)));

el.status.insertAdjacentHTML('beforeend', STATUSES.map((s) => `<option value="${s.value}">${escapeHtml(s.label)}</option>`).join(''));
el.search.addEventListener('input', () => { state.search = el.search.value; render(); });
el.status.addEventListener('input', () => { state.status = el.status.value; render(); });
el.precision.addEventListener('input', () => { state.precision = el.precision.value; render(); });
el.sort.addEventListener('input', () => { state.sort = el.sort.value; render(); });

function resetFilters() {
  Object.assign(state, { search: '', status: '', precision: 'all' });
  el.search.value = '';
  el.status.value = '';
  el.precision.value = 'all';
  render();
}

function renderLegend() {
  document.getElementById('map-legend').innerHTML =
    STATUSES.map((s) => `<li><span class="legend-pin accent-${s.value}" aria-hidden="true"></span>${s.label}</li>`).join('') +
    `<li><span class="legend-pin accent-Mixed" aria-hidden="true">2</span>Mehrere am selben Ort</li>
     <li><span class="legend-pin accent-Beworben is-approximate" aria-hidden="true"></span>Gestrichelt: ungefähr / nur Stadt</li>
     <li><span class="origin-pin" aria-hidden="true"></span>Startpunkt</li>`;
}

// ---------- Loading ----------

async function load() {
  el.error.textContent = '';
  renderOriginBar();
  try {
    const origin = state.origin ? { latitude: state.origin.latitude, longitude: state.origin.longitude } : null;
    const response = await api.queryMap(origin);
    state.items = response.items;
    state.hasOrigin = response.hasOrigin;
  } catch (error) {
    if (error.status === 400 && state.origin) {
      saveOrigin(null); // stored origin is invalid – remove it instead of failing forever
      el.error.textContent = 'Der gespeicherte Startpunkt war ungültig und wurde entfernt.';
      return load();
    }
    el.list.innerHTML = `<div class="tl-state is-error"><p>Die Standorte konnten nicht geladen werden: ${escapeHtml(error.message)}</p>
      <button type="button" class="btn btn-secondary btn-sm" data-retry>Erneut versuchen</button></div>`;
    el.list.querySelector('[data-retry]').addEventListener('click', load);
    return;
  }
  render();
  if (state.selectedId && !state.items.some((i) => i.id === state.selectedId)) state.selectedId = null;
}

renderLegend();
el.list.innerHTML = '<p class="tl-state">Standorte werden geladen …</p>';
requireUser().then((user) => {
  originStorageKey = originKey(user.id);
  state.origin = loadOrigin();
  load();
}).catch(() => { /* not logged in: redirected to the login page */ });
